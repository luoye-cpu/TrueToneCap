#define main capApplicationMain
#include "../src/cap/main.cpp"
#undef main
#include <QtTest>

class TestCapWorkflow : public QObject {
    Q_OBJECT
private slots:
    void desktopPixelsAndOverlay() {
        QWidget marker;
        marker.setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::WindowStaysOnTopHint);
        marker.setStyleSheet("background:white;");
        marker.setGeometry(QRect(qApp->primaryScreen()->geometry().center()-QPoint(150,150),QSize(300,300)));
        marker.show();
        QVERIFY(QTest::qWaitForWindowExposed(&marker));
        QTest::qWait(500);
        QString error;
        const auto desktop=cap::captureScreen(marker.screen(),&error);
        QVERIFY2(!desktop.isNull(),qPrintable(error));
        const auto point=(marker.geometry().center()-marker.screen()->geometry().topLeft())*marker.devicePixelRatioF();
        const auto pixel=desktop.pixelColor(point);
        const auto display=cap::preview(desktop);
        qInfo()<<"marker"<<pixel<<"preview"<<display.pixelColor(point)<<"format"<<desktop.format();
        QVERIFY(pixel.redF()>.1f && pixel.greenF()>.1f && pixel.blueF()>.1f);
        QVERIFY(display.pixelColor(point).red()>80);
        QImage delivered;
        QPointer<CaptureOverlay> overlay=new CaptureOverlay(marker.screen(),desktop,
            [&](QImage image,bool,bool,bool){delivered=image;});
        const auto close=qScopeGuard([&]{if(overlay)overlay->close();});
        QVERIFY(QTest::qWaitForWindowExposed(overlay));
        const auto local=marker.geometry().center()-overlay->geometry().topLeft();
        QVERIFY(overlay->grab().toImage().pixelColor(local*overlay->devicePixelRatioF()).red()>40);
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,local-QPoint(40,40));
        QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,local+QPoint(40,40));
        for(auto *button:overlay->findChildren<QPushButton*>())if(button->text()==QStringLiteral("✓"))button->click();
        QVERIFY(!delivered.isNull());
        QVERIFY(cap::preview(delivered).pixelColor(delivered.rect().center()).red()>80);
        QTemporaryDir dir;const auto path=dir.filePath("marker.png");
        QVERIFY(savePng(delivered,path));
        QVERIFY(QImageReader(path).read().pixelColor(delivered.rect().center()).redF()>.1f);
    }
    void selectAnnotateAndCopy() {
        QImage source(640,480,QImage::Format_RGBA32FPx4);
        source.fill(QColor::fromRgbF(1,1,1));source.setColorSpace(QColorSpace::SRgbLinear);
        source.setText("capHdr","scRGB-FP16");
        QImage delivered;bool copied=false;
        QPointer<CaptureOverlay> overlay=new CaptureOverlay(qApp->primaryScreen(),source,
            [&](QImage image,bool pin,bool avif,bool copy){delivered=image;copied=copy;QVERIFY(!pin);QVERIFY(!avif);});
        QVERIFY(QTest::qWaitForWindowExposed(overlay));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(50,50));
        QTest::mouseMove(overlay,QPoint(350,250));
        QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(350,250));
        const auto buttons=overlay->findChildren<QPushButton*>();QCOMPARE(buttons.size(),11);
        QPushButton *rectangle=nullptr,*check=nullptr;
        for(auto *button:buttons){QVERIFY(button->isVisible());if(button->text()==QStringLiteral("矩形"))rectangle=button;if(button->text()==QStringLiteral("✓"))check=button;}
        QVERIFY(rectangle && check);rectangle->click();
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));
        QTest::mouseMove(overlay,QPoint(250,200));
        QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(250,200));
        check->click();QVERIFY(copied);QVERIFY(!delivered.isNull());
        QCOMPARE(delivered.format(),QImage::Format_RGBA32FPx4);
        QCOMPARE(delivered.text("capHdr"),QString("scRGB-FP16"));
        bool ink=false,original=false;
        for(int y=0;y<delivered.height();++y)for(int x=0;x<delivered.width();++x){
            const auto color=delivered.pixelColor(x,y);ink|=color.redF()>.9f && color.greenF()<.1f;original|=color.greenF()>.9f;
        }
        QVERIFY(ink && original);
        const auto png=QImage::fromData(QApplication::clipboard()->mimeData()->data("image/png"));
        QCOMPARE(png.depth(),64);QCOMPARE(png.size(),delivered.size());
        QTRY_VERIFY(!overlay);
    }
    void selectedToolsLineAndUndo() {
        QImage source(640,480,QImage::Format_RGBA32FPx4);source.fill(QColor::fromRgbF(.4,.5,.6));
        source.setColorSpace(QColorSpace::SRgbLinear);source.setText("capHdr","scRGB-FP16");
        QImage delivered;QPointer<CaptureOverlay> overlay=new CaptureOverlay(qApp->primaryScreen(),source,[&](QImage image,bool,bool,bool){delivered=image;});
        const auto close=qScopeGuard([&]{if(overlay)overlay->close();});QVERIFY(QTest::qWaitForWindowExposed(overlay));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(50,50));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(450,350));
        const auto button=[&](const QString &name){for(auto *b:overlay->findChildren<QPushButton*>())if(b->text()==name)return b;return static_cast<QPushButton*>(nullptr);};
        auto *line=button(QStringLiteral("线")),*rectangle=button(QStringLiteral("矩形"));QVERIFY(line && rectangle);
        auto *options=overlay->findChild<QComboBox*>("capToolOptions");QVERIFY(options);line->click();QVERIFY(line->isChecked());QVERIFY(!rectangle->isChecked());
        QCOMPARE(options->itemText(0),QStringLiteral("直线"));QCOMPARE(options->itemText(1),QStringLiteral("箭头"));options->setCurrentIndex(1);
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));QTest::mouseMove(overlay,QPoint(300,230));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(300,230));
        QVERIFY(button(QStringLiteral("撤销"))->isEnabled());overlay->grab().save("build/acceptance-1.0.10/cap-selected-arrow.png");
        QTest::keyClick(overlay,Qt::Key_Z,Qt::ControlModifier);QTRY_VERIFY(!button(QStringLiteral("撤销"))->isEnabled());
        rectangle->click();QVERIFY(rectangle->isChecked());QVERIFY(!line->isChecked());QVERIFY(options->currentText().contains("px"));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(110,120));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(300,230));button(QStringLiteral("撤销"))->click();
        auto *copy=button(QStringLiteral("✓"));QVERIFY(copy);copy->click();QVERIFY(!delivered.isNull());
        for(int y=0;y<delivered.height();++y)for(int x=0;x<delivered.width();++x){const auto pixel=delivered.pixelColor(x,y);QVERIFY(std::abs(pixel.redF()-.4f)<1e-5);QVERIFY(std::abs(pixel.greenF()-.5f)<1e-5);QVERIFY(std::abs(pixel.blueF()-.6f)<1e-5);}
    }
    void editableTextObjects() {
        QImage source(1000,700,QImage::Format_RGBA32FPx4);source.fill(QColor::fromRgbF(.4,.5,1.5));
        source.setColorSpace(QColorSpace::SRgbLinear);source.setText("capHdr","scRGB-FP16");
        QImage delivered;QPointer<CaptureOverlay> overlay=new CaptureOverlay(qApp->primaryScreen(),source,[&](QImage image,bool,bool,bool){delivered=image;});
        const auto close=qScopeGuard([&]{if(overlay)overlay->close();});overlay->resize(source.size());QVERIFY(QTest::qWaitForWindowExposed(overlay));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(40,40));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(900,500));
        const auto button=[&](const QString &name){for(auto *b:overlay->findChildren<QPushButton*>())if(b->text()==name)return b;return static_cast<QPushButton*>(nullptr);};
        button(QStringLiteral("文字"))->click();QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,120));
        auto boxes=overlay->findChildren<QWidget*>("capTextBox");QCOMPARE(boxes.size(),1);auto *first=static_cast<CaptureTextBox*>(boxes.first());
        QVERIFY(first->isVisible());QVERIFY(first->editor->hasFocus());QVERIFY(!qApp->activeModalWidget());
        QTest::keyClicks(first->editor,"First");QCOMPARE(first->editor->toPlainText(),QString("First"));
        QTest::keyClick(first->editor,Qt::Key_Z,Qt::ControlModifier);QCOMPARE(first->editor->toPlainText(),QString());
        QTest::keyClicks(first->editor,"Hello");first->editor->insertPlainText(QStringLiteral("\n世界"));
        auto *family=overlay->findChild<QFontComboBox*>("capFontFamily");auto *size=overlay->findChild<QSpinBox*>("capFontSize");
        auto *bold=overlay->findChild<QToolButton*>("capFontBold"),*italic=overlay->findChild<QToolButton*>("capFontItalic");
        QVERIFY(family && size && bold && italic);QVERIFY(family->isVisible() && family->isEditable());
        const auto families=QFontDatabase::families();QVERIFY(!families.isEmpty());const auto name=families.contains("Arial")?QString("Arial"):families.first();
        family->completer()->setCompletionPrefix(name.left(2));QVERIFY(family->completer()->completionCount()>0);family->setCurrentFont(QFont(name));
        auto *number=size->findChild<QLineEdit*>();QVERIFY(number);number->setFocus();number->selectAll();QTest::keyClicks(number,"32");QTest::keyClick(number,Qt::Key_Enter);
        QCOMPARE(size->value(),32);bold->click();italic->click();QCOMPARE(first->editor->font().family(),name);
        QCOMPARE(first->editor->font().pointSize(),32);QVERIFY(first->editor->font().bold());QVERIFY(first->editor->font().italic());
        QTest::keyClick(first->editor,Qt::Key_Z,Qt::ControlModifier);QVERIFY(!first->editor->font().italic());italic->click();
        QVERIFY(first->editor->viewport()->height()>=first->editor->document()->size().height());
        const auto moveBox=[](CaptureTextBox *box,const QPoint &delta){
            const QPoint start(3,3);QTest::mousePress(box,Qt::LeftButton,Qt::NoModifier,start);
            QMouseEvent event(QEvent::MouseMove,QPointF(start+delta),QPointF(box->mapToGlobal(start+delta)),Qt::NoButton,Qt::LeftButton,Qt::NoModifier);
            QApplication::sendEvent(box,&event);QTest::mouseRelease(box,Qt::LeftButton,Qt::NoModifier,start);
        };
        const auto origin=first->pos();moveBox(first,QPoint(60,45));QCOMPARE(first->pos(),origin+QPoint(60,45));
        button(QStringLiteral("撤销"))->click();QCOMPARE(first->pos(),origin);QCOMPARE(first->editor->toPlainText(),QStringLiteral("Hello\n世界"));
        moveBox(first,QPoint(40,30));button(QStringLiteral("矩形"))->click();QVERIFY(!family->isVisible());
        QTest::mouseClick(first->editor->viewport(),Qt::LeftButton);QVERIFY(button(QStringLiteral("文字"))->isChecked());QVERIFY(family->isVisible());
        first->editor->selectAll();first->editor->insertPlainText(QStringLiteral("Revised\n文字"));
        QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(600,150));boxes=overlay->findChildren<QWidget*>("capTextBox");QCOMPARE(boxes.size(),2);
        auto *second=static_cast<CaptureTextBox*>(boxes.last());QTest::keyClicks(second->editor,"Other");size->setValue(14);QCOMPARE(second->editor->font().pointSize(),14);QCOMPARE(first->editor->font().pointSize(),32);
        second->editor->setPlainText(QString(250,'W'));QVERIFY(QRect(QPoint(40,40),QPoint(900,500)).contains(second->geometry()));
        QVERIFY(second->editor->document()->size().height()>QFontMetrics(second->editor->font()).height()*2);
        QTest::keyClick(second->editor,Qt::Key_Z,Qt::ControlModifier);QCOMPARE(second->editor->toPlainText(),QString("Other"));QCOMPARE(second->pos(),QPoint(600,150));
        QTest::mouseClick(first->editor->viewport(),Qt::LeftButton);QCOMPARE(size->value(),32);QVERIFY(bold->isChecked() && italic->isChecked());
        QDir().mkpath("build/acceptance-cap-text");overlay->grab().save("build/acceptance-cap-text/editable-text.png");
        button(QStringLiteral("✓"))->click();QVERIFY(!delivered.isNull());QCOMPARE(delivered.format(),QImage::Format_RGBA32FPx4);QCOMPARE(delivered.text("capHdr"),QString("scRGB-FP16"));
        bool firstInk=false,secondInk=false;
        for(int y=0;y<delivered.height();++y)for(int x=0;x<delivered.width();++x){
            const auto pixel=delivered.pixelColor(x,y);QVERIFY(!(pixel.redF()>.9 && pixel.greenF()>.9 && pixel.blueF()>.9));
            if(pixel.redF()>.9 && pixel.greenF()<.1){if(x<400)firstInk=true;else secondInk=true;}
        }
        QVERIFY(firstInk && secondInk);QCOMPARE(delivered.pixelColor(60,80),source.pixelColor(100,120));
        QVERIFY(savePng(delivered,"build/acceptance-cap-text/exported-text.png"));
    }
    void textAndDrawingUndo() {
        QImage source(800,600,QImage::Format_RGBA8888);source.fill(Qt::white);QImage delivered;
        QPointer<CaptureOverlay> overlay=new CaptureOverlay(qApp->primaryScreen(),source,[&](QImage image,bool,bool,bool){delivered=image;});overlay->resize(source.size());
        const auto close=qScopeGuard([&]{if(overlay)overlay->close();});QVERIFY(QTest::qWaitForWindowExposed(overlay));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(20,20));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(700,400));
        const auto button=[&](const QString &name){for(auto *b:overlay->findChildren<QPushButton*>())if(b->text()==name)return b;return static_cast<QPushButton*>(nullptr);};
        button(QStringLiteral("文字"))->click();QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));
        auto *box=overlay->findChild<QTextEdit*>("capTextEditor");QTest::keyClicks(box,"Undo me");button(QStringLiteral("矩形"))->click();
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(350,150));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(500,250));
        button(QStringLiteral("撤销"))->click();QCOMPARE(box->toPlainText(),QString("Undo me"));
        QTest::keyClick(box,Qt::Key_Z,Qt::ControlModifier);QCOMPARE(box->toPlainText(),QString());
        QTest::keyClick(box,Qt::Key_Z,Qt::ControlModifier);QTRY_VERIFY(overlay->findChildren<QTextEdit*>("capTextEditor").isEmpty());
        QVERIFY(!button(QStringLiteral("撤销"))->isEnabled());button(QStringLiteral("PNG"))->click();QCOMPARE(delivered,source.copy(QRect(QPoint(20,20),QPoint(700,400))));
    }
    void toolbarShapesAndTextFrames() {
        QImage source(1000,700,QImage::Format_RGBA8888);source.fill(QColor(70,80,90));
        QPointer<CaptureOverlay> overlay=new CaptureOverlay(qApp->primaryScreen(),source,[](QImage,bool,bool,bool){});overlay->resize(source.size());
        const auto close=qScopeGuard([&]{if(overlay)overlay->close();});QVERIFY(QTest::qWaitForWindowExposed(overlay));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(40,40));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(900,500));
        const auto button=[&](const QString &name){for(auto *b:overlay->findChildren<QPushButton*>())if(b->text()==name)return b;return static_cast<QPushButton*>(nullptr);};
        QCOMPARE(overlay->findChild<QWidget*>("capToolBar")->cursor().shape(),Qt::ArrowCursor);
        QCOMPARE(button(QStringLiteral("✓"))->toolTip(),QStringLiteral("复制到剪贴板"));
        QVERIFY(button(QStringLiteral("矩形"))->toolTip().contains("Ctrl"));QVERIFY(button(QStringLiteral("圆形"))->toolTip().contains("Ctrl"));
        auto *width=overlay->findChild<QSpinBox*>("capPenWidth");QVERIFY(width && width->isVisible());QCOMPARE(width->value(),3);QCOMPARE(width->suffix(),QString(" px"));
        auto *number=width->findChild<QLineEdit*>();number->selectAll();QTest::keyClicks(number,"7");QTest::keyClick(number,Qt::Key_Enter);QCOMPARE(width->value(),7);
        const auto inkBounds=[&]{
            const auto image=overlay->grab().toImage().scaled(overlay->size(),Qt::IgnoreAspectRatio,Qt::FastTransformation);QRect bounds;
            for(int y=50;y<490;++y)for(int x=50;x<890;++x){const auto pixel=image.pixelColor(x,y);if(pixel.red()>230 && pixel.green()<60)bounds=bounds.united(QRect(x,y,1,1));}
            return bounds;
        };
        for(const auto &tool:{QStringLiteral("矩形"),QStringLiteral("圆形")}){
            button(tool)->click();QCOMPARE(overlay->findChild<QComboBox*>("capToolOptions")->currentText(),QString("7 px"));QTest::mousePress(overlay,Qt::LeftButton,Qt::ControlModifier,QPoint(100,100));
            QTest::mouseRelease(overlay,Qt::LeftButton,Qt::ControlModifier,QPoint(300,200));const auto ink=inkBounds();
            QVERIFY(!ink.isEmpty());QVERIFY(qAbs(ink.width()-ink.height())<=1);QVERIFY(ink.width()<120);button(QStringLiteral("撤销"))->click();
        }
        button(QStringLiteral("线"))->click();auto *options=overlay->findChild<QComboBox*>("capToolOptions");QCOMPARE(options->count(),3);options->setCurrentText(QStringLiteral("虚线"));
        QTest::mousePress(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));QTest::mouseRelease(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(400,100));
        auto image=overlay->grab().toImage().scaled(overlay->size(),Qt::IgnoreAspectRatio,Qt::FastTransformation);int transitions=0;bool previous=false;
        for(int x=100;x<=400;++x){const auto pixel=image.pixelColor(x,100);const bool ink=pixel.red()>230 && pixel.green()<60;if(ink!=previous)++transitions;previous=ink;}
        QVERIFY(transitions>6);button(QStringLiteral("撤销"))->click();
        button(QStringLiteral("文字"))->click();auto *size=overlay->findChild<QSpinBox*>("capFontSize");
        QStyleOptionSpinBox option;option.initFrom(size);option.buttonSymbols=QAbstractSpinBox::UpDownArrows;option.stepEnabled=QAbstractSpinBox::StepUpEnabled | QAbstractSpinBox::StepDownEnabled;
        const auto up=size->style()->subControlRect(QStyle::CC_SpinBox,&option,QStyle::SC_SpinBoxUp,size),down=size->style()->subControlRect(QStyle::CC_SpinBox,&option,QStyle::SC_SpinBoxDown,size);
        QVERIFY(!up.isEmpty() && !down.isEmpty() && !up.intersects(down));QTest::mouseClick(size,Qt::LeftButton,Qt::NoModifier,up.center());QCOMPARE(size->value(),19);
        QTest::mouseClick(size,Qt::LeftButton,Qt::NoModifier,down.center());QCOMPARE(size->value(),18);
        QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(100,120));QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(300,120));
        QTRY_COMPARE(overlay->findChildren<QWidget*>("capTextBox").size(),1);auto *box=static_cast<CaptureTextBox*>(overlay->findChild<QWidget*>("capTextBox"));QCOMPARE(box->pos(),QPoint(300,120));
        QTest::keyClicks(box->editor,"Frame");QTest::mouseClick(overlay,Qt::LeftButton,Qt::NoModifier,QPoint(600,120));QTest::mouseClick(button(QStringLiteral("画笔")),Qt::LeftButton);
        QTRY_COMPARE(overlay->findChildren<QWidget*>("capTextBox").size(),1);QCOMPARE(box->editor->toPlainText(),QString("Frame"));
        const auto globalStart=box->mapToGlobal(QPoint(3,3));QTest::mousePress(box,Qt::LeftButton,Qt::NoModifier,QPoint(3,3));
        for(int step=1;step<=6;++step){
            const auto global=globalStart-QPoint(step*10,step*5);
            QMouseEvent event(QEvent::MouseMove,QPointF(box->mapFromGlobal(global)),QPointF(global),Qt::NoButton,Qt::LeftButton,Qt::NoModifier);QApplication::sendEvent(box,&event);QTest::qWait(10);
            image=overlay->grab().toImage().scaled(overlay->size(),Qt::IgnoreAspectRatio,Qt::FastTransformation).copy(box->geometry());
            const auto white=[&](int x,int y){const auto pixel=image.pixelColor(x,y);return pixel.red()>230 && pixel.green()>230 && pixel.blue()>230;};
            int top=0,bottom=0,left=0,right=0;
            for(int x=2;x<image.width()-2;++x){top+=white(x,2);bottom+=white(x,image.height()-3);}
            for(int y=2;y<image.height()-2;++y){left+=white(2,y);right+=white(image.width()-3,y);}
            QVERIFY2(top>2 && bottom>2 && left>2 && right>2,qPrintable(QString("frame %1: top %2 bottom %3 left %4 right %5").arg(step).arg(top).arg(bottom).arg(left).arg(right)));
        }
        QTest::mouseRelease(box,Qt::LeftButton,Qt::NoModifier,QPoint(3,3));QDir().mkpath("build/acceptance-cap-interaction");overlay->grab().save("build/acceptance-cap-interaction/text-frame.png");
    }
    void pinAspectZoomAndEditing() {
        QImage source(1600,400,QImage::Format_RGBA8888);source.fill(Qt::white);
        {QPainter painter(&source);for(int y=0;y<400;y+=10)for(int x=0;x<1600;x+=10)if((x/10+y/10)%2)painter.fillRect(x,y,10,10,Qt::black);}
        QImage delivered;bool avif=false;
        QPointer<PinWindow> pin=new PinWindow(source,[&](QImage image,bool,bool isAvif,bool){delivered=image;avif=isAvif;});
        const auto close=qScopeGuard([&]{if(pin)pin->close();});QVERIFY(QTest::qWaitForWindowExposed(pin));
        auto *view=pin->findChild<QGraphicsView*>("capPinView");QVERIFY(view);const double originalZoom=1.0/pin->screen()->devicePixelRatio();
        QCOMPARE(view->size(),QSize(qRound(1600*originalZoom),qRound(400*originalZoom)));QCOMPARE(view->transform().m11(),originalZoom);QCOMPARE(view->transform().m11(),view->transform().m22());
        QWheelEvent wheel(QPointF(view->viewport()->rect().center()),QPointF(view->viewport()->mapToGlobal(view->viewport()->rect().center())),QPoint(),QPoint(0,120),Qt::NoButton,Qt::NoModifier,Qt::NoScrollPhase,false);
        QApplication::sendEvent(view->viewport(),&wheel);QTest::qWait(20);QCOMPARE(view->size(),QSize(qRound(1920*originalZoom),qRound(480*originalZoom)));QCOMPARE(view->transform().m11(),view->transform().m22());
        QVERIFY(!view->renderHints().testFlag(QPainter::SmoothPixmapTransform));
        const auto pixels=view->viewport()->grab().toImage();int black=0,white=0;
        for(int y=3;y<pixels.height()-3;++y)for(int x=3;x<pixels.width()-3;++x){const auto pixel=pixels.pixelColor(x,y);QVERIFY(pixel==QColor(Qt::black) || pixel==QColor(Qt::white));black+=pixel==QColor(Qt::black);white+=pixel==QColor(Qt::white);}
        QVERIFY(black>100 && white>100);
        auto *menu=pin->findChild<QMenu*>("capPinMenu");QVERIFY(menu);const auto actions=menu->actions();QCOMPARE(actions.size(),5);
        QCOMPARE(actions[0]->text(),QStringLiteral("显示工具栏"));QCOMPARE(actions[1]->text(),QStringLiteral("复制"));QCOMPARE(actions[2]->text(),QStringLiteral("保存为PNG"));QCOMPARE(actions[3]->text(),QStringLiteral("保存为AVIF"));QCOMPARE(actions[4]->text(),QStringLiteral("销毁"));
        actions[2]->trigger();QCOMPARE(delivered,source);QVERIFY(!avif);actions[3]->trigger();QCOMPARE(delivered,source);QVERIFY(avif);
        actions[1]->trigger();QTRY_COMPARE(QImage::fromData(QApplication::clipboard()->mimeData()->data("image/png")).size(),source.size());QVERIFY(pin->isVisible());
        actions[0]->trigger();auto *bar=pin->findChild<QWidget*>("capToolBar");QVERIFY(bar && bar->isVisible());QCOMPARE(bar->cursor().shape(),Qt::ArrowCursor);
        const auto button=[&](const QString &name){for(auto *b:pin->findChildren<QPushButton*>())if(b->text()==name)return b;return static_cast<QPushButton*>(nullptr);};
        for(const auto &name:{QStringLiteral("画笔"),QStringLiteral("矩形"),QStringLiteral("圆形"),QStringLiteral("线"),QStringLiteral("文字"),QStringLiteral("撤销"),QStringLiteral("PNG"),QStringLiteral("AVIF"),QStringLiteral("✓")})QVERIFY(button(name) && button(name)->isVisible());
        QCOMPARE(button(QStringLiteral("✓"))->toolTip(),QStringLiteral("隐藏工具栏"));
        menu->popup(pin->mapToGlobal(QPoint(10,10)));QTRY_VERIFY(menu->isVisible());QCOMPARE(actions[0]->text(),QStringLiteral("隐藏工具栏"));menu->hide();
        actions[0]->trigger();QVERIFY(!bar->isVisible());actions[0]->trigger();QVERIFY(bar->isVisible());
        const auto clipboardBefore=QApplication::clipboard()->mimeData()->data("image/png");
        button(QStringLiteral("✓"))->click();QVERIFY(!bar->isVisible());QVERIFY(pin->isVisible());QCOMPARE(QApplication::clipboard()->mimeData()->data("image/png"),clipboardBefore);
        menu->popup(pin->mapToGlobal(QPoint(10,10)));QTRY_VERIFY(menu->isVisible());QCOMPARE(actions[0]->text(),QStringLiteral("显示工具栏"));menu->hide();actions[0]->trigger();
        auto *proxy=qgraphicsitem_cast<QGraphicsProxyWidget*>(view->scene()->items().first());QVERIFY(proxy);
        auto *canvas=static_cast<CaptureOverlay*>(proxy->widget());QVERIFY(canvas);button(QStringLiteral("文字"))->click();
        QTest::mouseClick(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));auto *editor=canvas->findChild<QTextEdit*>("capTextEditor");QVERIFY(editor);QTest::keyClicks(editor,"Pinned text");
        actions[2]->trigger();QCOMPARE(delivered.size(),source.size());QVERIFY(delivered!=source);QVERIFY(pin->isVisible());
        QTest::keyClick(editor,Qt::Key_Escape);QVERIFY(!bar->isVisible());QVERIFY(canvas->isVisible());actions[0]->trigger();
        button(QStringLiteral("撤销"))->click();button(QStringLiteral("撤销"))->click();actions[2]->trigger();QCOMPARE(delivered,source);
        button(QStringLiteral("矩形"))->click();QTest::mousePress(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));QTest::mouseRelease(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(250,200));actions[2]->trigger();QVERIFY(delivered!=source);
        pin->activateWindow();QVERIFY(QTest::qWaitForWindowActive(pin));view->setFocus();QTest::keyClick(view,Qt::Key_Z,Qt::ControlModifier);actions[2]->trigger();QCOMPARE(delivered,source);
        QTest::mousePress(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(100,100));QTest::mouseRelease(canvas,Qt::LeftButton,Qt::NoModifier,QPoint(250,200));
        bar->activateWindow();QVERIFY(QTest::qWaitForWindowActive(bar));QTest::keyClick(bar,Qt::Key_Z,Qt::ControlModifier);actions[2]->trigger();QCOMPARE(delivered,source);
        QDir().mkpath("build/acceptance-cap-interaction");pin->grab().save("build/acceptance-cap-interaction/pin-nearest.png");bar->grab().save("build/acceptance-cap-interaction/pin-toolbar.png");
        actions[4]->trigger();QTRY_VERIFY(!pin);
    }
    void pinScreenFitAndBorders() {
        auto *screen=QGuiApplication::screenAt(QCursor::pos());if(!screen)screen=qApp->primaryScreen();
        const auto available=screen->availableGeometry();
        const auto checkBorder=[](PinWindow *pin){
            const auto image=pin->grab().toImage();const QColor blue(70,135,200);
            for(int x=0;x<image.width();++x){QCOMPARE(image.pixelColor(x,0),blue);QCOMPARE(image.pixelColor(x,image.height()-1),blue);}
            for(int y=0;y<image.height();++y){QCOMPARE(image.pixelColor(0,y),blue);QCOMPARE(image.pixelColor(image.width()-1,y),blue);}
        };
        for(const auto size:{QSize(320,180),QSize(qRound(available.width()*screen->devicePixelRatio()*2),qRound(available.height()*screen->devicePixelRatio()*2))}){
            QImage source(size,QImage::Format_RGBA8888);source.fill(Qt::white);QImage delivered;
            QPointer<PinWindow> pin=new PinWindow(source,[&](QImage image,bool,bool,bool){delivered=image;});
            const auto close=qScopeGuard([&]{if(pin)pin->close();});QVERIFY(QTest::qWaitForWindowExposed(pin));
            auto *view=pin->findChild<QGraphicsView*>("capPinView");QVERIFY(view);
            QVERIFY(available.contains(pin->geometry()));QCOMPARE(view->transform().m11(),view->transform().m22());
            if(size.width()==320)QCOMPARE(view->transform().m11(),1.0/screen->devicePixelRatio());
            else{QVERIFY(view->transform().m11()<1.0/screen->devicePixelRatio());QVERIFY(pin->width()==available.width() || pin->height()==available.height());}
            checkBorder(pin);pin->move(pin->pos()+QPoint(10,10));QTest::qWait(20);checkBorder(pin);
            pin->findChild<QMenu*>("capPinMenu")->actions()[2]->trigger();QCOMPARE(delivered,source);
        }
    }
    void hdrClipboardAndPin() {
        QString error;const auto desktop=cap::captureScreen(qApp->primaryScreen(),&error);
        QVERIFY2(!desktop.isNull(),qPrintable(error));
        auto image=desktop.copy(QRect(0,0,256,256));image.setText("capHdr",desktop.text("capHdr"));
        copyImage(image);
        QTRY_VERIFY_WITH_TIMEOUT(QImage::fromData(QApplication::clipboard()->mimeData()->data("image/png")).size()==image.size(),5000);
        const auto *mime=QApplication::clipboard()->mimeData();
        QVERIFY(mime->hasImage());QVERIFY(mime->hasFormat("image/png"));
        const auto png=QImage::fromData(mime->data("image/png"));QCOMPARE(png.size(),image.size());
        if(image.text("capHdr")=="scRGB-FP16"){
            QCOMPARE(png.depth(),64);const auto raw=mime->data("application/x-vscap-scrgb");
            QCOMPARE(raw.size(),8+image.width()*image.height()*16);
            QCOMPARE(raw.mid(8,image.width()*16),QByteArray(reinterpret_cast<const char*>(image.constScanLine(0)),image.width()*16));
        }
        auto *pin=new PinWindow(image,[](QImage,bool,bool,bool){});QVERIFY(pin->windowFlags().testFlag(Qt::WindowStaysOnTopHint));
        pin->showToolBar();QCOMPARE(pin->findChildren<QPushButton*>().size(),11);pin->close();
    }
    void realHdrAvif() {
        QString error;const auto desktop=cap::captureScreen(qApp->primaryScreen(),&error);QVERIFY2(!desktop.isNull(),qPrintable(error));
        auto image=desktop.copy(QRect(0,0,128,128));image.setText("capHdr",desktop.text("capHdr"));
        QTemporaryDir dir;const auto png=dir.filePath("capture.png"),avif=dir.filePath("capture.avif");QVERIFY(savePng(image,png));
        QProcess process;process.start(QCoreApplication::applicationDirPath()+"/ffmpeg.exe",avifArguments(image,png,avif));
        QSignalSpy done(&process,QOverload<int,QProcess::ExitStatus>::of(&QProcess::finished));
        QTRY_VERIFY_WITH_TIMEOUT(!done.isEmpty(),30000);QCOMPARE(process.exitCode(),0);QVERIFY(QFileInfo(avif).size()>0);
        QProcess probe;probe.start(QCoreApplication::applicationDirPath()+"/ffprobe.exe",{"-v","error","-select_streams","v:0","-show_entries","stream=width,height,pix_fmt,color_primaries,color_transfer,color_range","-of","json",avif});
        QVERIFY(probe.waitForFinished(10000));const auto stream=QJsonDocument::fromJson(probe.readAllStandardOutput()).object().value("streams").toArray().first().toObject();
        QCOMPARE(stream.value("width").toInt(),128);QCOMPARE(stream.value("pix_fmt").toString(),QString("yuv444p10le"));
        if(image.text("capHdr")=="scRGB-FP16"){QCOMPARE(stream.value("color_transfer").toString(),QString("smpte2084"));QCOMPARE(stream.value("color_primaries").toString(),QString("bt2020"));QCOMPARE(stream.value("color_range").toString(),QString("pc"));}
    }
};
QTEST_MAIN(TestCapWorkflow)
#include "TestCapWorkflow.moc"
