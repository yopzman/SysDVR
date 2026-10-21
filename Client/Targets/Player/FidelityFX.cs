using System;
using System.Runtime.CompilerServices;

namespace SysDVR.Client.Targets.Player
{
    /// <summary>
    /// AMD FidelityFX RCAS (Robust Contrast Adaptive Sharpening) implementation for SysDVR.
    /// Operates directly on the luminance (Y) plane of decoded/upscaled video frames,
    /// providing high-frequency edge sharpening without ringing or halo artifacts.
    /// </summary>
    public static unsafe class FidelityFX
    {
        [ThreadStatic]
        private static byte[]? s_rowBuffer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ApplyRCAS(byte* yPlane, int width, int height, int stride, float sharpness = 0.22f)
        {
            if (yPlane == null || width <= 2 || height <= 2)
                return;

            int requiredBufferSize = width * 2;
            if (s_rowBuffer == null || s_rowBuffer.Length < requiredBufferSize)
                s_rowBuffer = new byte[requiredBufferSize];

            fixed (byte* tempBuffer = s_rowBuffer)
            {
                byte* prevRow = tempBuffer;
                byte* curRow = tempBuffer + width;

                // Copy first row into previous row buffer
                Buffer.MemoryCopy(yPlane, prevRow, width, width);

                int sharpnessFactor = (int)(Math.Clamp(sharpness, 0.05f, 0.5f) * 256);

                for (int y = 1; y < height - 1; y++)
                {
                    byte* rowCurrent = yPlane + (y * stride);
                    byte* rowNext = yPlane + ((y + 1) * stride);

                    // Cache current row before modifying it in-place
                    Buffer.MemoryCopy(rowCurrent, curRow, width, width);

                    for (int x = 1; x < width - 1; x++)
                    {
                        int pB = prevRow[x];       // North
                        int pD = curRow[x - 1];    // West
                        int pE = curRow[x];        // Center
                        int pF = curRow[x + 1];    // East
                        int pH = rowNext[x];       // South

                        // Find min and max in 5-pixel cross
                        int mn = pB;
                        if (pD < mn) mn = pD;
                        if (pF < mn) mn = pF;
                        if (pH < mn) mn = pH;
                        if (pE < mn) mn = pE;

                        int mx = pB;
                        if (pD > mx) mx = pD;
                        if (pF > mx) mx = pF;
                        if (pH > mx) mx = pH;
                        if (pE > mx) mx = pE;

                        // Noise-resistant contrast amplitude
                        int amp = Math.Min(mn, 255 - mx);
                        if (amp <= 0 || mx <= mn)
                            continue;

                        // Calculate adaptive weight (fixed point Q8)
                        int wQ8 = (amp * sharpnessFactor) / (mx - mn + 8);
                        if (wQ8 > 64) wQ8 = 64; // Clamp to max 0.25 (64/256)

                        // Filtered value: E' = (E + w * (B + D + F + H)) / (1 + 4w)
                        int sumCross = pB + pD + pF + pH;
                        int num = (pE << 8) + wQ8 * (sumCross - (pE << 2));
                        int result = num >> 8;

                        // Clamp to [0, 255]
                        if (result < 0) result = 0;
                        else if (result > 255) result = 255;

                        rowCurrent[x] = (byte)result;
                    }

                    // Swap row buffers
                    byte* tmp = prevRow;
                    prevRow = curRow;
                    curRow = tmp;
                }
            }
        }
    }
}
