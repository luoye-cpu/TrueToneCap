// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// The editor and pinned windows are compiled unchanged from VS-Cap.
#define main upstreamCapMain
#include "upstream/src/cap/main.cpp"
#undef main
#include <cstdio>

static constexpr quint32 frameMagic = 0x31435454; // TTC1

static QImage readFrame(const QString &path,QPoint *position) {
    QFile file(path);if(!file.open(QIODevice::ReadOnly))return {};
    QDataStream stream(&file);stream.setByteOrder(QDataStream::LittleEndian);
    quint32 magic; qint32 w,h,kind,x,y;stream>>magic>>w>>h>>kind>>x>>y;
    const qint64 rowBytes=qint64(w)*(kind==1?16:4);
    if(stream.status()!=QDataStream::Ok || magic!=frameMagic || w<=0 || h<=0 || (kind!=0 && kind!=1) || file.size()-24!=rowBytes*h)return {};
    QImage image(w,h,kind==1?QImage::Format_RGBA32FPx4:QImage::Format_ARGB32);
    if(image.isNull())return {};
    for(int row=0;row<h;++row)if(file.read(reinterpret_cast<char*>(image.scanLine(row)),rowBytes)!=rowBytes)return {};
    image.setColorSpace(kind==1?QColorSpace::SRgbLinear:QColorSpace::SRgb);
    image.setText("capHdr",kind==1?"scRGB-FP16":"SDR");
    if(position)*position=QPoint(x,y);return image;
}

static bool writeFrame(const QImage &source,const QString &path) {
    const bool hdr=source.text("capHdr")=="scRGB-FP16";
    const auto image=source.convertToFormat(hdr?QImage::Format_RGBA32FPx4:QImage::Format_ARGB32);
    QFile file(path);if(!file.open(QIODevice::WriteOnly))return false;
    QDataStream stream(&file);stream.setByteOrder(QDataStream::LittleEndian);
    stream<<frameMagic<<qint32(image.width())<<qint32(image.height())<<qint32(hdr?1:0)<<qint32(0)<<qint32(0);
    const qint64 rowBytes=qint64(image.width())*(hdr?16:4);
    for(int row=0;row<image.height();++row)if(file.write(reinterpret_cast<const char*>(image.constScanLine(row)),rowBytes)!=rowBytes)return false;
    return stream.status()==QDataStream::Ok && file.flush();
}

static void sendMessage(const QJsonObject &message) {
    const auto bytes=QJsonDocument(message).toJson(QJsonDocument::Compact)+'\n';
    std::fwrite(bytes.constData(),1,bytes.size(),stdout);std::fflush(stdout);
}

static QImage clipboardImage() {
    QImage image=QApplication::clipboard()->image();
    const auto raw=QApplication::clipboard()->mimeData()->data("application/x-vscap-scrgb");
    if(raw.size()>=8){
        QDataStream stream(raw);qint32 w=0,h=0;stream>>w>>h;
        if(w>0 && h>0 && qint64(w)*h*16==raw.size()-8){
            image=QImage(w,h,QImage::Format_RGBA32FPx4);
            if(image.isNull())return {};
            for(int y=0;y<h;++y)memcpy(image.scanLine(y),raw.constData()+8+qint64(y)*w*16,w*16);
            image.setColorSpace(QColorSpace::SRgbLinear);image.setText("capHdr","scRGB-FP16");
        }
    }
    return image;
}

#ifndef TTC_VSCAP_TEST
int main(int argc,char **argv) {
    QApplication app(argc,argv);app.setApplicationName("TrueToneCap VS-Cap");app.setQuitOnLastWindowClosed(false);
    QCommandLineParser parser;parser.addHelpOption();
    parser.addOptions({{"capture","Captured TTC frame","file"},{"paste","Pin clipboard image"},{"session","Private exchange directory","directory"},{"output-dir","Default export directory","directory"},{"parent-pid","TTC process ID","pid"}});
    parser.process(app);
    if(parser.isSet("capture")==parser.isSet("paste") || !QDir(parser.value("session")).exists())return 2;
    const auto session=parser.value("session");
    QSet<QWidget*> windows;
    const auto track=[&](QWidget *window,bool selection){
        windows.insert(window);
        QObject::connect(window,&QObject::destroyed,&app,[&,window,selection]{
            windows.remove(window);if(selection)sendMessage({{"event","selection-closed"}});
            // Allow VS-Cap's four 100 ms clipboard retries to finish before shutdown.
            if(windows.isEmpty())QTimer::singleShot(600,&app,[&]{if(windows.isEmpty())app.quit();});
        });
    };
    std::function<void(QImage,bool,bool,bool)> output;
    output=[&](QImage image,bool pin,bool avif,bool copy){
        if(pin){track(new PinWindow(std::move(image),output),false);return;}
        QString destination;
        if(!copy){
            const auto ext=avif?"avif":"png";
            destination=QFileDialog::getSaveFileName(nullptr,QStringLiteral("TrueToneCap 保存截图"),QDir(parser.value("output-dir")).filePath(QDateTime::currentDateTime().toString("yyyyMMdd-HHmmss-zzz")+"."+ext),avif?"AVIF (*.avif)":"PNG (*.png)");
            if(destination.isEmpty())return;
        }
        const auto frame=QDir(session).filePath(QUuid::createUuid().toString(QUuid::WithoutBraces)+".frame");
        if(!writeFrame(image,frame)){sendMessage({{"event","error"},{"message","Cannot write edited pixels"}});return;}
        sendMessage({{"event","export"},{"frame",frame},{"destination",destination},{"avif",avif},{"copy",copy}});
    };
    QPoint position;
    const auto image=parser.isSet("paste")?clipboardImage():readFrame(parser.value("capture"),&position);
    if(image.isNull()){sendMessage({{"event","error"},{"message",parser.isSet("paste")?"Clipboard has no image":"Invalid captured frame"}});return 3;}
    if(parser.isSet("paste"))track(new PinWindow(image,output),false);
    else {
        QScreen *screen=nullptr;MONITORINFOEXW info{};info.cbSize=sizeof(info);
        const POINT point{position.x()+1,position.y()+1};
        if(GetMonitorInfoW(MonitorFromPoint(point,MONITOR_DEFAULTTONEAREST),&info))
            for(auto *candidate:app.screens())if(candidate->name()==QString::fromWCharArray(info.szDevice)){screen=candidate;break;}
        if(!screen)screen=QGuiApplication::screenAt(QCursor::pos());if(!screen)screen=app.primaryScreen();
        auto *overlay=new CaptureOverlay(screen,image,output);
        overlay->setWindowTitle(QStringLiteral("TrueToneCap VS-Cap 截图"));track(overlay,true);
    }
    const auto parent=OpenProcess(SYNCHRONIZE,FALSE,parser.value("parent-pid").toUInt());
    QTimer parentTimer;
    if(parent){QObject::connect(&parentTimer,&QTimer::timeout,&app,[&]{if(WaitForSingleObject(parent,0)==WAIT_OBJECT_0)app.quit();});parentTimer.start(1000);}
    const int result=app.exec();if(parent)CloseHandle(parent);return result;
}
#endif
