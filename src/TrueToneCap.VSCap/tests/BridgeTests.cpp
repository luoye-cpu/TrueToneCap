// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
#define TTC_VSCAP_TEST
#include "../main.cpp"
#include <QtTest>

class BridgeTests : public QObject {
    Q_OBJECT
private slots:
    void invalidFrame() {
        QTemporaryDir directory;const auto path=directory.filePath("bad.frame");
        QFile file(path);QVERIFY(file.open(QIODevice::WriteOnly));file.write("TTC1");file.close();
        QVERIFY(readFrame(path,nullptr).isNull());
        QVERIFY(file.open(QIODevice::WriteOnly));QDataStream stream(&file);stream.setByteOrder(QDataStream::LittleEndian);
        stream<<frameMagic<<qint32(20)<<qint32(20)<<qint32(1)<<qint32(0)<<qint32(0);file.close();
        QVERIFY(readFrame(path,nullptr).isNull());
    }
    void crossLanguagePixels() {
        const auto directory=qEnvironmentVariable("TTC_VSCAP_FIXTURES");
        if(directory.isEmpty())QSKIP("Run tests/VSCapInterop first and set TTC_VSCAP_FIXTURES");
        QPoint position;
        const auto sdr=readFrame(QDir(directory).filePath("csharp-sdr.frame"),&position);
        QVERIFY(!sdr.isNull());QCOMPARE(position,QPoint(-1920,120));QCOMPARE(sdr.size(),QSize(2,1));
        QCOMPARE(sdr.pixel(0,0),qRgba(3,2,1,255));QCOMPARE(sdr.pixel(1,0),qRgba(6,5,4,128));
        QVERIFY(writeFrame(sdr,QDir(directory).filePath("qt-sdr.frame")));
        const auto hdr=readFrame(QDir(directory).filePath("csharp-hdr.frame"),nullptr);
        QVERIFY(!hdr.isNull());QCOMPARE(hdr.format(),QImage::Format_RGBA32FPx4);
        const float expected[]{-.25f,.5f,8.f,1.f,2.f,0.f,.125f,.75f};
        QCOMPARE(memcmp(hdr.constBits(),expected,sizeof(expected)),0);
        QVERIFY(writeFrame(hdr,QDir(directory).filePath("qt-hdr.frame")));
    }
    void editedHdrExport() {
        QImage image(400,300,QImage::Format_RGBA32FPx4);image.fill(QColor::fromRgbF(2,2,2));
        image.setColorSpace(QColorSpace::SRgbLinear);image.setText("capHdr","scRGB-FP16");
        QImage delivered;
        QPointer<PinWindow> pin=new PinWindow(image,[&](QImage result,bool,bool,bool){delivered=result;});
        pin->showToolBar();auto *view=pin->findChild<QGraphicsView*>("capPinView");QVERIFY(view);
        CaptureOverlay *canvas=nullptr;
        // QGraphicsProxyWidget owns the canvas outside QWidget's child tree.
        for(auto *item:view->scene()->items())if(auto *proxy=qgraphicsitem_cast<QGraphicsProxyWidget*>(item))canvas=static_cast<CaptureOverlay*>(proxy->widget());
        QVERIFY(canvas);
        for(auto *button:canvas->findChildren<QPushButton*>())if(button->text()==QStringLiteral("矩形"))button->click();
        QTest::mousePress(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(50,50));
        QTest::mouseRelease(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(150,150));
        canvas->exportImage(false,false);QVERIFY(!delivered.isNull());QCOMPARE(delivered.format(),QImage::Format_RGBA32FPx4);
        const auto untouched=reinterpret_cast<const float*>(delivered.constScanLine(250));QCOMPARE(untouched[0],2.f);
        // 上面两行挡不住"一笔都没画进去"：格式对、未改动行当然还是 2.0。
        // 既然真的拖了一个矩形（50,50→150,150），导出图与纯净原图之间必须有像素差 ⇒ 数出来。
        int changedRows=0;
        for(int y=0;y<image.height();++y){
            const auto *d=reinterpret_cast<const float*>(delivered.constScanLine(y));
            const auto *o=reinterpret_cast<const float*>(image.constScanLine(y));
            for(int i=0;i<image.width()*4;++i)if(d[i]!=o[i]){++changedRows;break;}
        }
        QVERIFY2(changedRows>0,"画了一笔却没有任何像素改变 ⇒ 编辑结果没进导出");
        QTemporaryDir directory;const auto path=directory.filePath("edited.frame");QVERIFY(writeFrame(delivered,path));
        const auto imported=readFrame(path,nullptr);QCOMPARE(imported,delivered);
        canvas->undoEdit();canvas->exportImage(false,false);QCOMPARE(delivered,image);
        pin->close();QCoreApplication::sendPostedEvents(nullptr,QEvent::DeferredDelete);
    }
};
QTEST_MAIN(BridgeTests)
#include "BridgeTests.moc"
