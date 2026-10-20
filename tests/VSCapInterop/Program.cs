// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
using TrueToneCap.App.Services;
using System.Runtime.InteropServices;

string directory = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "ttc-vscap-interop-tests");
Directory.CreateDirectory(directory);
byte[] sdr = [1, 2, 3, 255, 4, 5, 6, 128];
float[] hdr = [-.25f, .5f, 8f, 1f, 2f, 0f, .125f, .75f];
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
if (args.FirstOrDefault() == "--verify-qt")
{
    Check(VSCapFrame.Read(Path.Combine(directory, "qt-sdr.frame")).SdrPixels!.SequenceEqual(sdr), "Qt to C# BGRA channels and alpha");
    Check(MemoryMarshal.AsBytes(VSCapFrame.Read(Path.Combine(directory, "qt-hdr.frame")).HdrPixels!.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(hdr.AsSpan())), "Qt to C# exact HDR samples, negative values and highlights");
    return;
}
new VSCapFrame(2, 1, sdr, null).Write(Path.Combine(directory, "csharp-sdr.frame"), -1920, 120);
new VSCapFrame(2, 1, null, hdr).Write(Path.Combine(directory, "csharp-hdr.frame"), 0, 0);
Check(VSCapFrame.Read(Path.Combine(directory, "csharp-sdr.frame")).SdrPixels!.SequenceEqual(sdr), "SDR round trip");
Check(VSCapFrame.Read(Path.Combine(directory, "csharp-hdr.frame")).HdrPixels!.SequenceEqual(hdr), "HDR round trip");
var broken = Path.Combine(directory, "broken.frame");
File.WriteAllBytes(broken, File.ReadAllBytes(Path.Combine(directory, "csharp-hdr.frame"))[..^1]);
try { VSCapFrame.Read(broken); throw new Exception("Truncated frame was accepted"); } catch (InvalidDataException) { Console.WriteLine("PASS truncated frame rejected"); }
try { new VSCapFrame(2, 1, [0], null).Write(broken, 0, 0); throw new Exception("Invalid pixels were accepted"); } catch (InvalidDataException) { Console.WriteLine("PASS invalid pixels rejected"); }
