#!/usr/bin/env python3
"""Analyze PNG chunk data for TrueToneCap encoder output.
Generates a test PNG and prints all chunks with hex dumps."""

import struct
import zlib
import os

def read_png_chunks(path):
    """Read and return all PNG chunks."""
    with open(path, 'rb') as f:
        signature = f.read(8)
        assert signature == b'\x89PNG\r\n\x1a\n', "Not a valid PNG file"
        
        chunks = []
        while True:
            length_bytes = f.read(4)
            if not length_bytes:
                break
            length = struct.unpack('>I', length_bytes)[0]
            chunk_type = f.read(4)
            data = f.read(length) if length > 0 else b''
            crc = f.read(4)
            
            chunks.append({
                'type': chunk_type.decode('ascii', errors='replace'),
                'length': length,
                'data': data,
                'crc': crc.hex()
            })
            
            if chunk_type == b'IEND':
                break
                
    return chunks

def print_chunk_info(chunks):
    """Print formatted chunk info."""
    print(f"\n{'='*70}")
    print(f"PNG Chunk Analysis - {len(chunks)} chunks")
    print(f"{'='*70}")
    
    for i, c in enumerate(chunks):
        print(f"\n  [{i}] {c['type']} ({c['length']} bytes)")
        print(f"       CRC: {c['crc']}")
        
        if c['type'] == 'IHDR':
            w, h = struct.unpack('>II', c['data'][0:8])
            bit_depth = c['data'][8]
            color_type = c['data'][9]
            comp = c['data'][10]
            filter_m = c['data'][11]
            interlace = c['data'][12]
            color_names = {0: 'Greyscale', 2: 'Truecolor', 3: 'Indexed', 4: 'Greyscale+Alpha', 6: 'Truecolor+Alpha'}
            print(f"       Width: {w}, Height: {h}")
            print(f"       Bit depth: {bit_depth}")
            print(f"       Color type: {color_type} ({color_names.get(color_type, 'unknown')})")
            print(f"       Compression: {comp}, Filter: {filter_m}, Interlace: {interlace}")
            
            # VALIDATION: Check if bit_depth is legal for color_type
            if color_type == 6:
                if bit_depth not in (8, 16):
                    print(f"       ⚠️  WARNING: bit_depth={bit_depth} is INVALID for color_type=6! Only 8 and 16 allowed.")
                else:
                    print(f"       ✅ bit_depth={bit_depth} valid for color_type=6")
                    
        elif c['type'] == 'sBIT':
            print(f"       sBIT data: {c['data'].hex()} -> {list(c['data'])}")
            sbit_vals = list(c['data'])
            if len(sbit_vals) == 4:
                print(f"       R:{sbit_vals[0]} G:{sbit_vals[1]} B:{sbit_vals[2]} A:{sbit_vals[3]} significant bits")
                
        elif c['type'] == 'cICP':
            primaries = c['data'][0]
            transfer = c['data'][1]
            matrix = c['data'][2]
            full_range = c['data'][3]
            primaries_names = {1: 'BT.709/sRGB', 9: 'BT.2020', 12: 'Display P3'}
            transfer_names = {1: 'BT.709', 13: 'sRGB', 16: 'ST.2084 PQ', 18: 'HLG'}
            print(f"       Color Primaries: {primaries} ({primaries_names.get(primaries, 'unknown')})")
            print(f"       Transfer Function: {transfer} ({transfer_names.get(transfer, 'unknown')})")
            print(f"       Matrix: {matrix}, Full Range: {full_range}")
            
        elif c['type'] == 'iCCP':
            # ICC profile name + compression method
            null_idx = c['data'].index(0)
            prof_name = c['data'][:null_idx].decode('ascii', errors='replace')
            comp_method = c['data'][null_idx + 1]
            print(f"       Profile name: {prof_name}")
            print(f"       Compression: {comp_method}")
            
        elif c['type'] == 'IDAT':
            print(f"       IDAT data (compressed, {c['length']} bytes)")
            
        elif c['type'] == 'IEND':
            print(f"       End of image")
            
        else:
            # Print hex dump for other chunks
            hex_str = c['data'][:16].hex()
            ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in c['data'][:16])
            print(f"       Data: {hex_str} | {ascii_str}")

def generate_test_png():
    """Generate test PNG files with all bit depths using the actual encoder."""
    # First check if previous test files exist
    temp_dir = os.environ.get('TEMP', '/tmp')
    out_dir = os.path.join(temp_dir, 'TrueToneCap_PngTest')
    os.makedirs(out_dir, exist_ok=True)
    return out_dir

def main():
    out_dir = generate_test_png()
    print(f"Output directory: {out_dir}")
    
    # Check if any test files exist
    png_files = [f for f in os.listdir(out_dir) if f.endswith('.png')]
    if png_files:
        print(f"\nFound {len(png_files)} PNG files in output directory")
        for fname in sorted(png_files):
            fpath = os.path.join(out_dir, fname)
            print(f"\n{'#'*70}")
            print(f"# FILE: {fname} ({os.path.getsize(fpath)} bytes)")
            print(f"{'#'*70}")
            chunks = read_png_chunks(fpath)
            print_chunk_info(chunks)
    else:
        print(f"\nNo PNG files found. The test must be run first.")
        print(f"Run: dotnet run --project src/TrueToneCap.Test")
        print(f"\nOr manually check the published app output directory.")
    
    # Also check the publish directory
    publish_dir = r'c:\PLAN\TrueToneCap\publish\TrueToneCap-v0.3.0-beta'
    if os.path.exists(publish_dir):
        png_files2 = [f for f in os.listdir(publish_dir) if f.endswith('.png')]
        if png_files2:
            print(f"\n\nFound {len(png_files2)} PNG files in publish directory")
            for fname in sorted(png_files2):
                fpath = os.path.join(publish_dir, fname)
                print(f"\n{'#'*70}")
                print(f"# FILE: {fname} ({os.path.getsize(fpath)} bytes)")
                print(f"{'#'*70}")
                try:
                    chunks = read_png_chunks(fpath)
                    print_chunk_info(chunks)
                except Exception as e:
                    print(f"  Error reading: {e}")

if __name__ == '__main__':
    main()