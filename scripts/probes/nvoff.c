// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// scripts/probes/nvoff.c
//
// 【为什么留这个文件】NvEncoderNative.cs 里那批硬编码的 struct 尺寸与函数表索引，
// 出处就是这里：凭注释/文档猜偏移错过一次（错得离谱，见 NvEncoderNative.cs:43 那条注释），
// 之后一律以 MSVC 实测的 sizeof()/offsetof() 为准。
//
// 【怎么复跑】需要 Windows SDK 的 nvEncodeAPI.h（随 NVIDIA Video Codec SDK 分发）：
//   cl /I <path-to>/Interface scripts\probes\nvoff.c && nvoff.exe > nvoff.txt
// 然后把输出与 NvEncoderNative.cs 里的常量逐条对，不要手改数字。
//
// 拿不到官方 SDK 时可用 FFmpeg 维护的同一份头文件（2026-09-20 从一次性脚本 build-nvoff.ps1
// 折进这里，那个脚本硬编码了 cl.exe 的具体版本号，搬进仓库只会烂掉）：
//   https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/master/include/ffnvcodec/nvEncodeAPI.h
// ⚠ 本仓库的 shell 是 MSYS bash，curl/python 在沙箱里出不了网；宿主 PowerShell 的
//   Invoke-WebRequest 可以 ⇒ 下载这一步要用 powershell 做，别在 bash 里试。


#include <windows.h>
#include <stdio.h>
#include <stddef.h>
#include "nvEncodeAPI.h"

#define SZ(t)        printf("sizeof(%s) = %zu\n", #t, (size_t)sizeof(t))
#define O(t, m)      printf("%-52s %6zu\n", #t "." #m, (size_t)offsetof(t, m))
#define SEP(s)       printf("\n--- %s ---\n", s)

int main(void)
{
    SZ(NV_ENC_INITIALIZE_PARAMS);
    SZ(NV_ENC_CONFIG);
    SZ(NV_ENC_RC_PARAMS);
    SZ(NV_ENC_CONFIG_AV1);
    SZ(NV_ENC_PRESET_CONFIG);
    SZ(NV_ENC_REGISTER_RESOURCE);
    SZ(NV_ENC_MAP_INPUT_RESOURCE);
    SZ(NV_ENC_CREATE_INPUT_BUFFER);
    SZ(NV_ENC_PIC_PARAMS);
    SZ(NV_ENC_LOCK_BITSTREAM);
    SZ(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS);

    SEP("NV_ENC_INITIALIZE_PARAMS");
    O(NV_ENC_INITIALIZE_PARAMS, version);
    O(NV_ENC_INITIALIZE_PARAMS, encodeGUID);
    O(NV_ENC_INITIALIZE_PARAMS, presetGUID);
    O(NV_ENC_INITIALIZE_PARAMS, encodeWidth);
    O(NV_ENC_INITIALIZE_PARAMS, encodeHeight);
    O(NV_ENC_INITIALIZE_PARAMS, darWidth);
    O(NV_ENC_INITIALIZE_PARAMS, darHeight);
    O(NV_ENC_INITIALIZE_PARAMS, frameRateNum);
    O(NV_ENC_INITIALIZE_PARAMS, frameRateDen);
    O(NV_ENC_INITIALIZE_PARAMS, enableEncodeAsync);
    O(NV_ENC_INITIALIZE_PARAMS, enablePTD);
    O(NV_ENC_INITIALIZE_PARAMS, privDataSize);
    O(NV_ENC_INITIALIZE_PARAMS, privData);
    O(NV_ENC_INITIALIZE_PARAMS, encodeConfig);
    O(NV_ENC_INITIALIZE_PARAMS, maxEncodeWidth);
    O(NV_ENC_INITIALIZE_PARAMS, maxEncodeHeight);
    O(NV_ENC_INITIALIZE_PARAMS, maxMEHintCountsPerBlock);
    O(NV_ENC_INITIALIZE_PARAMS, tuningInfo);
    O(NV_ENC_INITIALIZE_PARAMS, bufferFormat);
    O(NV_ENC_INITIALIZE_PARAMS, numStateBuffers);
    O(NV_ENC_INITIALIZE_PARAMS, outputStatsLevel);

    SEP("NV_ENC_CONFIG");
    O(NV_ENC_CONFIG, version);
    O(NV_ENC_CONFIG, profileGUID);
    O(NV_ENC_CONFIG, gopLength);
    O(NV_ENC_CONFIG, frameIntervalP);
    O(NV_ENC_CONFIG, monoChromeEncoding);
    O(NV_ENC_CONFIG, frameFieldMode);
    O(NV_ENC_CONFIG, mvPrecision);
    O(NV_ENC_CONFIG, rcParams);
    O(NV_ENC_CONFIG, encodeCodecConfig);

    SEP("NV_ENC_RC_PARAMS");
    O(NV_ENC_RC_PARAMS, version);
    O(NV_ENC_RC_PARAMS, rateControlMode);
    O(NV_ENC_RC_PARAMS, constQP);
    O(NV_ENC_RC_PARAMS, averageBitRate);
    O(NV_ENC_RC_PARAMS, maxBitRate);
    O(NV_ENC_RC_PARAMS, vbvBufferSize);
    O(NV_ENC_RC_PARAMS, vbvInitialDelay);
    O(NV_ENC_RC_PARAMS, targetQuality);
    O(NV_ENC_RC_PARAMS, targetQualityLSB);
    O(NV_ENC_RC_PARAMS, lookaheadDepth);
    O(NV_ENC_RC_PARAMS, multiPass);
    O(NV_ENC_RC_PARAMS, alphaLayerBitrateRatio);

    SEP("NV_ENC_CONFIG_AV1 (relative to av1Config base)");
    O(NV_ENC_CONFIG_AV1, level);
    O(NV_ENC_CONFIG_AV1, tier);
    O(NV_ENC_CONFIG_AV1, minPartSize);
    O(NV_ENC_CONFIG_AV1, maxPartSize);
    O(NV_ENC_CONFIG_AV1, idrPeriod);
    O(NV_ENC_CONFIG_AV1, intraRefreshPeriod);
    O(NV_ENC_CONFIG_AV1, intraRefreshCnt);
    O(NV_ENC_CONFIG_AV1, maxNumRefFramesInDPB);
    O(NV_ENC_CONFIG_AV1, numTileColumns);
    O(NV_ENC_CONFIG_AV1, numTileRows);
    O(NV_ENC_CONFIG_AV1, reserved2);
    O(NV_ENC_CONFIG_AV1, tileWidths);
    O(NV_ENC_CONFIG_AV1, tileHeights);
    O(NV_ENC_CONFIG_AV1, maxTemporalLayersMinus1);
    O(NV_ENC_CONFIG_AV1, colorPrimaries);
    O(NV_ENC_CONFIG_AV1, transferCharacteristics);
    O(NV_ENC_CONFIG_AV1, matrixCoefficients);
    O(NV_ENC_CONFIG_AV1, colorRange);
    O(NV_ENC_CONFIG_AV1, chromaSamplePosition);
    O(NV_ENC_CONFIG_AV1, useBFramesAsRef);
    O(NV_ENC_CONFIG_AV1, filmGrainParams);
    O(NV_ENC_CONFIG_AV1, numFwdRefs);
    O(NV_ENC_CONFIG_AV1, numBwdRefs);
    O(NV_ENC_CONFIG_AV1, outputBitDepth);
    O(NV_ENC_CONFIG_AV1, inputBitDepth);
    O(NV_ENC_CONFIG_AV1, ltrNumFrames);
    O(NV_ENC_CONFIG_AV1, numTemporalLayers);
    O(NV_ENC_CONFIG_AV1, tfLevel);
    O(NV_ENC_CONFIG_AV1, reserved1);
    O(NV_ENC_CONFIG_AV1, reserved3);

    SEP("ABSOLUTE within NV_ENC_CONFIG");
    printf("%-52s %6zu\n", "cfg.encodeCodecConfig.av1Config",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config));
    printf("%-52s %6zu\n", "cfg.rcParams.rateControlMode",
           (size_t)offsetof(NV_ENC_CONFIG, rcParams.rateControlMode));
    printf("%-52s %6zu\n", "cfg.rcParams.constQP",
           (size_t)offsetof(NV_ENC_CONFIG, rcParams.constQP));
    printf("%-52s %6zu\n", "cfg.rcParams.averageBitRate",
           (size_t)offsetof(NV_ENC_CONFIG, rcParams.averageBitRate));
    printf("%-52s %6zu\n", "cfg.rcParams.targetQuality",
           (size_t)offsetof(NV_ENC_CONFIG, rcParams.targetQuality));
    printf("%-52s %6zu\n", "cfg...av1Config.level",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.level));
    printf("%-52s %6zu\n", "cfg...av1Config.idrPeriod",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.idrPeriod));
    printf("%-52s %6zu\n", "cfg...av1Config.chromaFormatIDC_bitfieldWord",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.idrPeriod) - (size_t)sizeof(uint32_t));
    printf("%-52s %6zu\n", "cfg...av1Config.outputBitDepth",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.outputBitDepth));
    printf("%-52s %6zu\n", "cfg...av1Config.inputBitDepth",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.inputBitDepth));
    printf("%-52s %6zu\n", "cfg...av1Config.colorPrimaries",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.colorPrimaries));
    printf("%-52s %6zu\n", "cfg...av1Config.transferCharacteristics",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.transferCharacteristics));
    printf("%-52s %6zu\n", "cfg...av1Config.matrixCoefficients",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.matrixCoefficients));
    printf("%-52s %6zu\n", "cfg...av1Config.colorRange",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.colorRange));

    SEP("NV_ENC_PRESET_CONFIG");
    O(NV_ENC_PRESET_CONFIG, version);
    O(NV_ENC_PRESET_CONFIG, presetCfg);

    SEP("NV_ENC_PIC_PARAMS");
    O(NV_ENC_PIC_PARAMS, version);
    O(NV_ENC_PIC_PARAMS, inputWidth);
    O(NV_ENC_PIC_PARAMS, inputHeight);
    O(NV_ENC_PIC_PARAMS, inputPitch);
    O(NV_ENC_PIC_PARAMS, encodePicFlags);
    O(NV_ENC_PIC_PARAMS, frameIdx);
    O(NV_ENC_PIC_PARAMS, inputTimeStamp);
    O(NV_ENC_PIC_PARAMS, inputDuration);
    O(NV_ENC_PIC_PARAMS, inputBuffer);
    O(NV_ENC_PIC_PARAMS, outputBitstream);
    O(NV_ENC_PIC_PARAMS, completionEvent);
    O(NV_ENC_PIC_PARAMS, pictureType);
    O(NV_ENC_PIC_PARAMS, codecPicParams);

    SEP("NV_ENC_CREATE_INPUT_BUFFER");
    O(NV_ENC_CREATE_INPUT_BUFFER, version);
    O(NV_ENC_CREATE_INPUT_BUFFER, width);
    O(NV_ENC_CREATE_INPUT_BUFFER, height);
    O(NV_ENC_CREATE_INPUT_BUFFER, memoryHeap);
    O(NV_ENC_CREATE_INPUT_BUFFER, bufferFmt);
    O(NV_ENC_CREATE_INPUT_BUFFER, inputBuffer);

    SEP("NV_ENC_REGISTER_RESOURCE");
    O(NV_ENC_REGISTER_RESOURCE, version);
    O(NV_ENC_REGISTER_RESOURCE, resourceType);
    O(NV_ENC_REGISTER_RESOURCE, resourceToRegister);
    O(NV_ENC_REGISTER_RESOURCE, width);
    O(NV_ENC_REGISTER_RESOURCE, height);
    O(NV_ENC_REGISTER_RESOURCE, pitch);
    O(NV_ENC_REGISTER_RESOURCE, bufferFormat);
    O(NV_ENC_REGISTER_RESOURCE, registeredResource);

    SEP("NV_ENC_MAP_INPUT_RESOURCE");
    O(NV_ENC_MAP_INPUT_RESOURCE, version);
    O(NV_ENC_MAP_INPUT_RESOURCE, registeredResource);
    O(NV_ENC_MAP_INPUT_RESOURCE, mappedResource);
    O(NV_ENC_MAP_INPUT_RESOURCE, mappedBufferFmt);

    SEP("NV_ENC_LOCK_BITSTREAM");
    O(NV_ENC_LOCK_BITSTREAM, version);
    O(NV_ENC_LOCK_BITSTREAM, outputBitstream);
    O(NV_ENC_LOCK_BITSTREAM, bitstreamBufferPtr);
    O(NV_ENC_LOCK_BITSTREAM, bitstreamSizeInBytes);
    O(NV_ENC_LOCK_BITSTREAM, pictureType);

    SEP("NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS");
    O(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS, version);
    O(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS, deviceType);
    O(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS, device);
    O(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS, apiVersion);

    SZ(NV_ENCODE_API_FUNCTION_LIST);
    O(NV_ENCODE_API_FUNCTION_LIST, version);
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncOpenEncodeSession);
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncInitializeEncoder);

    SZ(NV_ENC_LOCK_INPUT_BUFFER);
    O(NV_ENC_LOCK_INPUT_BUFFER, version);
    O(NV_ENC_LOCK_INPUT_BUFFER, inputBuffer);
    O(NV_ENC_LOCK_INPUT_BUFFER, bufferDataPtr);
    O(NV_ENC_LOCK_INPUT_BUFFER, pitch);

    SZ(NV_ENC_CREATE_BITSTREAM_BUFFER);
    O(NV_ENC_CREATE_BITSTREAM_BUFFER, version);
    O(NV_ENC_CREATE_BITSTREAM_BUFFER, size);
    O(NV_ENC_CREATE_BITSTREAM_BUFFER, bitstreamBuffer);
    O(NV_ENC_CREATE_BITSTREAM_BUFFER, memoryHeap);

    SEP("REGISTER_RESOURCE / MAP_INPUT_RESOURCE (fields used)");
    O(NV_ENC_REGISTER_RESOURCE, subResourceIndex);
    O(NV_ENC_REGISTER_RESOURCE, bufferUsage);
    O(NV_ENC_REGISTER_RESOURCE, pInputFencePoint);
    printf("%-52s %6zu\n", "cfg...av1Config.numFwdRefs",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.numFwdRefs));
    printf("%-52s %6zu\n", "cfg...av1Config.idrPeriod",
           (size_t)offsetof(NV_ENC_CONFIG, encodeCodecConfig.av1Config.idrPeriod));

    SEP("STRUCT VERSION MACROS (hex)");
#define PV(m) printf("%-44s 0x%08X\n", #m, (unsigned)(m))
    PV(NVENCAPI_VERSION);
    PV(NV_ENC_CONFIG_VER);
    PV(NV_ENC_INITIALIZE_PARAMS_VER);
    PV(NV_ENC_PRESET_CONFIG_VER);
    PV(NV_ENC_PIC_PARAMS_VER);
    PV(NV_ENC_RC_PARAMS_VER);
    PV(NV_ENC_CREATE_INPUT_BUFFER_VER);
    PV(NV_ENC_LOCK_BITSTREAM_VER);
    PV(NV_ENC_MAP_INPUT_RESOURCE_VER);
    PV(NV_ENC_REGISTER_RESOURCE_VER);
    PV(NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER);
    PV(NV_ENC_CAPS_PARAM_VER);

    SEP("FUNCTION LIST OFFSETS (probe)");
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncGetInputFormatCount);
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncGetInputFormats);
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncGetEncodeCaps);
    O(NV_ENCODE_API_FUNCTION_LIST, nvEncGetSequenceParamEx);

    SEP("10-BIT / INPUT FORMAT CONSTANTS (probe)");
    printf("%-44s %d\n", "NV_ENC_CAPS_SUPPORT_10BIT_ENCODE", (int)NV_ENC_CAPS_SUPPORT_10BIT_ENCODE);
    printf("%-44s %d\n", "NV_ENC_CAPS_SUPPORT_YUV444_ENCODE", (int)NV_ENC_CAPS_SUPPORT_YUV444_ENCODE);
    printf("%-44s %d\n", "NV_ENC_CAPS_SUPPORT_YUV422_ENCODE", (int)NV_ENC_CAPS_SUPPORT_YUV422_ENCODE);
    printf("%-44s 0x%X\n", "FMT_NV12", (unsigned)NV_ENC_BUFFER_FORMAT_NV12);
    printf("%-44s 0x%X\n", "FMT_YUV420_10BIT", (unsigned)NV_ENC_BUFFER_FORMAT_YUV420_10BIT);
    printf("%-44s 0x%X\n", "FMT_YUV444", (unsigned)NV_ENC_BUFFER_FORMAT_YUV444);
    printf("%-44s 0x%X\n", "FMT_YUV444_10BIT", (unsigned)NV_ENC_BUFFER_FORMAT_YUV444_10BIT);
    printf("%-44s 0x%X\n", "FMT_ARGB", (unsigned)NV_ENC_BUFFER_FORMAT_ARGB);
    printf("%-44s 0x%X\n", "FMT_ARGB10", (unsigned)NV_ENC_BUFFER_FORMAT_ARGB10);
    printf("%-44s 0x%X\n", "FMT_ABGR", (unsigned)NV_ENC_BUFFER_FORMAT_ABGR);
    printf("%-44s 0x%X\n", "FMT_ABGR10", (unsigned)NV_ENC_BUFFER_FORMAT_ABGR10);
    printf("%-44s 0x%X\n", "FMT_AYUV", (unsigned)NV_ENC_BUFFER_FORMAT_AYUV);
    printf("%-44s 0x%X\n", "FMT_P210", (unsigned)NV_ENC_BUFFER_FORMAT_P210);

    SEP("ENUMS");
    printf("NV_ENC_BIT_DEPTH: INVALID=%d 8=%d 10=%d\n",
           NV_ENC_BIT_DEPTH_INVALID, NV_ENC_BIT_DEPTH_8, NV_ENC_BIT_DEPTH_10);
    printf("NV_ENC_PARAMS_RC_CONSTQP=%d VBR=%d CBR=%d\n",
           NV_ENC_PARAMS_RC_CONSTQP, NV_ENC_PARAMS_RC_VBR, NV_ENC_PARAMS_RC_CBR);
    printf("NV_ENC_BUFFER_FORMAT_ARGB=0x%X ARGB10=0x%X NV16=0x%X\n",
           NV_ENC_BUFFER_FORMAT_ARGB, NV_ENC_BUFFER_FORMAT_ARGB10, NV_ENC_BUFFER_FORMAT_NV16);
    printf("NV_ENC_VUI_COLOR_PRIMARIES: BT709=%d BT2020=%d UNSPECIFIED=%d\n",
           NV_ENC_VUI_COLOR_PRIMARIES_BT709, NV_ENC_VUI_COLOR_PRIMARIES_BT2020,
           NV_ENC_VUI_COLOR_PRIMARIES_UNSPECIFIED);
    printf("NV_ENC_VUI_TRANSFER: BT709=%d SMPTE2084(PQ)=%d SRGB=%d UNSPECIFIED=%d\n",
           NV_ENC_VUI_TRANSFER_CHARACTERISTIC_BT709, NV_ENC_VUI_TRANSFER_CHARACTERISTIC_SMPTE2084,
           NV_ENC_VUI_TRANSFER_CHARACTERISTIC_SRGB, NV_ENC_VUI_TRANSFER_CHARACTERISTIC_UNSPECIFIED);
    printf("NV_ENC_VUI_MATRIX: RGB=%d BT709=%d BT2020_NCL=%d UNSPECIFIED=%d\n",
           NV_ENC_VUI_MATRIX_COEFFS_RGB, NV_ENC_VUI_MATRIX_COEFFS_BT709,
           NV_ENC_VUI_MATRIX_COEFFS_BT2020_NCL, NV_ENC_VUI_MATRIX_COEFFS_UNSPECIFIED);
    printf("NV_ENC_AV1_PART_SIZE: AUTO=%d 8x8=%d 64x64=%d\n",
           NV_ENC_AV1_PART_SIZE_AUTOSELECT, NV_ENC_AV1_PART_SIZE_8x8, NV_ENC_AV1_PART_SIZE_64x64);

    return 0;
}
