#include "DesktopCapture.h"
#include <QScreen>
#include <QtGui/qscreen_platform.h>
#include <QPixmap>
#include <QColorSpace>
#include <QImageWriter>
#include <QFile>
#include <QTransform>
#include <QElapsedTimer>
#include <QtCore/qfloat16.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstring>

using Microsoft::WRL::ComPtr;
namespace cap {
QImage captureScreen(QScreen *screen, QString *error)
{
    ComPtr<IDXGIFactory1> factory;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT a=0; factory->EnumAdapters1(a,&adapter)==S_OK; ++a,adapter.Reset()) {
            ComPtr<IDXGIOutput> output;
            for (UINT o=0; adapter->EnumOutputs(o,&output)==S_OK; ++o,output.Reset()) {
                DXGI_OUTPUT_DESC desc{};
                output->GetDesc(&desc);
                const auto *native=screen->nativeInterface<QNativeInterface::QWindowsScreen>();
                if (!native || desc.Monitor!=native->handle()) continue;
                ComPtr<IDXGIOutput6> output6;
                DXGI_OUTPUT_DESC1 color{};
                const bool hdr=SUCCEEDED(output.As(&output6)) && SUCCEEDED(output6->GetDesc1(&color)) &&
                    color.ColorSpace==DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                if (!hdr) break;
                // DuplicateOutput1 preserves the desktop's FP16 scRGB surface.
                // The older DuplicateOutput always converts to 8-bit BGRA.
                ComPtr<ID3D11Device> device;
                ComPtr<ID3D11DeviceContext> context;
                ComPtr<IDXGIOutput5> output5;
                ComPtr<IDXGIOutputDuplication> duplicate;
                const DXGI_FORMAT format=DXGI_FORMAT_R16G16B16A16_FLOAT;
                HRESULT hr=D3D11CreateDevice(adapter.Get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
                if (SUCCEEDED(hr)) hr=output.As(&output5);
                if (SUCCEEDED(hr)) hr=output5->DuplicateOutput1(device.Get(),0,1,&format,&duplicate);
                DXGI_OUTDUPL_FRAME_INFO info{};
                ComPtr<IDXGIResource> resource;
                if (SUCCEEDED(hr)) {
                    QElapsedTimer wait;wait.start();
                    do {
                        hr=duplicate->AcquireNextFrame(UINT(qMax<qint64>(1,1000-wait.elapsed())),&info,&resource);
                        if (FAILED(hr) || info.LastPresentTime.QuadPart!=0) break;
                        // A pointer-only notification can precede the first desktop
                        // image. Its texture is not a screenshot (often all black).
                        duplicate->ReleaseFrame();resource.Reset();
                        hr=DXGI_ERROR_WAIT_TIMEOUT;
                    } while (wait.elapsed()<1000);
                }
                if (FAILED(hr)) {
                    *error=QStringLiteral("HDR 桌面采集失败（0x%1）；请检查显示会话和显卡驱动。").arg(quint32(hr),8,16,QChar('0'));
                    return {};
                }
                ComPtr<ID3D11Texture2D> texture,staging;
                hr=resource.As(&texture);
                D3D11_TEXTURE2D_DESC td{};
                if (SUCCEEDED(hr)) texture->GetDesc(&td);
                if (SUCCEEDED(hr) && td.Format!=format) hr=E_FAIL;
                td.Usage=D3D11_USAGE_STAGING;
                td.BindFlags=0;td.MiscFlags=0;td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                if (SUCCEEDED(hr)) hr=device->CreateTexture2D(&td,nullptr,&staging);
                D3D11_MAPPED_SUBRESOURCE mapped{};
                if (SUCCEEDED(hr)) {
                    context->CopyResource(staging.Get(),texture.Get());
                    hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);
                }
                QImage image;
                if (SUCCEEDED(hr)) {
                    image=QImage(td.Width,td.Height,QImage::Format_RGBA32FPx4);
                    for (UINT y=0;y<td.Height;++y) {
                        const auto *src=reinterpret_cast<const qfloat16*>(static_cast<const char*>(mapped.pData)+y*mapped.RowPitch);
                        auto *dst=reinterpret_cast<float*>(image.scanLine(y));
                        for (UINT x=0;x<td.Width;++x) {
                            for (int c=0;c<3;++c) dst[x*4+c]=float(src[x*4+c]);
                            dst[x*4+3]=1.0f;
                        }
                    }
                    context->Unmap(staging.Get(),0);
                    int angle=0;
                    if (desc.Rotation==DXGI_MODE_ROTATION_ROTATE90) angle=90;
                    if (desc.Rotation==DXGI_MODE_ROTATION_ROTATE180) angle=180;
                    if (desc.Rotation==DXGI_MODE_ROTATION_ROTATE270) angle=270;
                    if (angle) image=image.transformed(QTransform().rotate(angle));
                    image.setColorSpace(QColorSpace::SRgbLinear);
                    image.setText("capHdr","scRGB-FP16");
                }
                duplicate->ReleaseFrame();
                if (image.isNull()) *error=QStringLiteral("HDR 桌面未提供可读取的 FP16 图像。");
                return image;
            }
        }
    }
    auto image=screen->grabWindow(0).toImage();
    image.setDevicePixelRatio(1);
    image.setColorSpace(QColorSpace::SRgb);
    image.setText("capHdr","SDR");
    return image;
}

static double pq(double linear)
{
    const double p=std::pow(std::clamp(linear,0.0,1.0),2610.0/16384.0);
    return std::pow((3424.0/4096.0+2413.0/128.0*p)/(1.0+2392.0/128.0*p),2523.0/32.0);
}

QImage pngPixels(const QImage &image)
{
    if (image.text("capHdr")!="scRGB-FP16") return image;
    QImage output(image.size(),QImage::Format_RGBA64);
    for (int y=0;y<image.height();++y) {
        const auto *src=reinterpret_cast<const float*>(image.constScanLine(y));
        auto *dst=reinterpret_cast<QRgba64*>(output.scanLine(y));
        for (int x=0;x<image.width();++x) {
            const double r=src[x*4],g=src[x*4+1],b=src[x*4+2];
            // scRGB 1.0 = 80 cd/m2. Map linear sRGB primaries to BT.2020,
            // then encode absolute luminance with ST 2084 into 16-bit PNG.
            const auto encode=[](double v){return quint16(std::lround(pq(v*.008)*65535.0));};
            dst[x]=QRgba64::fromRgba64(encode(.627404*r+.329283*g+.043313*b),
                encode(.069097*r+.919540*g+.011362*b),encode(.016391*r+.088013*g+.895595*b),65535);
        }
    }
    output.setColorSpace(QColorSpace::Bt2100Pq);
    return output;
}

QImage preview(const QImage &image)
{
    if (image.text("capHdr")!="scRGB-FP16") return image;
    auto output=image.copy();
    for (int y=0;y<output.height();++y) {
        auto *row=reinterpret_cast<float*>(output.scanLine(y));
        for (int x=0;x<output.width();++x) {
            const float l=std::max(0.0f,.2126f*row[x*4]+.7152f*row[x*4+1]+.0722f*row[x*4+2]);
            const float scale=1.0f/(1.0f+l);
            for (int c=0;c<3;++c) row[x*4+c]=std::max(0.0f,row[x*4+c]*scale);
        }
    }
    return output.convertedToColorSpace(QColorSpace::SRgb,QImage::Format_RGBA8888);
}

bool savePng(const QImage &image,const QString &path)
{
    QImageWriter writer(path,"png");writer.setCompression(9);
    if (!writer.write(pngPixels(image))) return false;
    if (image.text("capHdr")!="scRGB-FP16") return true;
    // PNG compression is lossless; PQ's integer representation is quantized.
    // Keep exact captured linear samples beside it for precision workflows.
    QFile raw(path+".scrgb-f32");
    if (!raw.open(QIODevice::WriteOnly)) return false;
    for (int y=0;y<image.height();++y)
        if (raw.write(reinterpret_cast<const char*>(image.constScanLine(y)),image.width()*16)!=image.width()*16) return false;
    QFile metadata(path+".json");
    if (!metadata.open(QIODevice::WriteOnly)) return false;
    return metadata.write(QString("{\"width\":%1,\"height\":%2,\"channels\":4,\"rawFormat\":\"RGBA-float32-little-endian\",\"rawColorSpace\":\"linear-sRGB-80nits\",\"pngColorSpace\":\"BT.2100-PQ\"}\n").arg(image.width()).arg(image.height()).toUtf8())>0;
}
}
