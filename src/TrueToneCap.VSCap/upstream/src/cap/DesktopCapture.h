#pragma once
#include <QImage>
#include <QString>
class QScreen;

namespace cap {
QImage captureScreen(QScreen *screen, QString *error);
QImage preview(const QImage &image);
QImage pngPixels(const QImage &image);
bool savePng(const QImage &image, const QString &path);
}
