using System;
using System.Drawing;
using System.Drawing.Imaging;

public class ImageProcessor
{
     // LUT (Look-Up Table) tömbök a szürkítéshez 
    private static byte[] lutR = new byte[256];
    private static byte[] lutG = new byte[256];
    private static byte[] lutB = new byte[256];
    private static bool lutInitialized = false;

    // LUT inicializálása (egyszer fut le)
    private static void InitLUT()
    {
        if (lutInitialized) return;

        for (int i = 0; i < 256; i++)
        {
            // Az emberi szem érzékenységéhez igazított súlyok (Luma formula)
            lutR[i] = (byte)(i * 0.299);
            lutG[i] = (byte)(i * 0.587);
            lutB[i] = (byte)(i * 0.114);
        }
        lutInitialized = true;
    }

    /// <summary>
     /// Kép szürkeárnyalatosítása villámgyors pointerekkel és LUT használatával[cite: 15, 24, 26].
                /// </summary>
    public static unsafe Bitmap ConvertToGrayscaleFast(Bitmap original)
    {
        InitLUT();

        // Létrehozunk egy új 8-bites (vagy 24-bites, de szürke) képet a memóriában
        Bitmap grayBitmap = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);

        using (FastBitmap fastOriginal = new FastBitmap(original))
        using (FastBitmap fastGray = new FastBitmap(grayBitmap))
        {
            fastOriginal.Lock();
            fastGray.Lock();

            for (int y = 0; y < fastOriginal.Height; y++)
            {
                for (int x = 0; x < fastOriginal.Width; x++)
                {
                    byte* origPixel = fastOriginal.GetPixelPointer(x, y);
                    byte* grayPixel = fastGray.GetPixelPointer(x, y);

                    // BGR sorrend a memóriában
                    byte b = origPixel[0];
                    byte g = origPixel[1];
                    byte r = origPixel[2];

                     // LUT alapú gyors szürkítés (nincs szorzás futásidőben!) 
                    byte grayValue = (byte)(lutB[b] + lutG[g] + lutR[r]);

                    // Eredmény beírása az új képbe (R, G, B csatornák ugyanazt kapják)
                    grayPixel[0] = grayValue;
                    grayPixel[1] = grayValue;
                    grayPixel[2] = grayValue;
                }
            }

            fastOriginal.Unlock();
            fastGray.Unlock();
        }

        return grayBitmap;
    }

    /// <summary>
     /// Egyszerű 3x3-as Gauss-szűrő a zajcsökkentésre.
                /// </summary>
    public static unsafe Bitmap ApplyGaussianBlur(Bitmap original)
    {
        Bitmap blurredBitmap = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);

        // 3x3 Gauss kernel
        int[,] kernel = {
            { 1, 2, 1 },
            { 2, 4, 2 },
            { 1, 2, 1 }
        };
        int kernelWeight = 16; // A mátrix elemeinek összege

        using (FastBitmap fastOriginal = new FastBitmap(original))
        using (FastBitmap fastBlurred = new FastBitmap(blurredBitmap))
        {
            fastOriginal.Lock();
            fastBlurred.Lock();

            // A széleket (1 pixel) kihagyjuk a túlcsordulás elkerülése végett
            for (int y = 1; y < fastOriginal.Height - 1; y++)
            {
                for (int x = 1; x < fastOriginal.Width - 1; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;

                    // Konvolúció a szomszédos pixeleken
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            byte* neighborPixel = fastOriginal.GetPixelPointer(x + kx, y + ky);
                            int weight = kernel[ky + 1, kx + 1];

                            sumB += neighborPixel[0] * weight;
                            sumG += neighborPixel[1] * weight;
                            sumR += neighborPixel[2] * weight;
                        }
                    }

                    byte* blurredPixel = fastBlurred.GetPixelPointer(x, y);
                    blurredPixel[0] = (byte)(sumB / kernelWeight);
                    blurredPixel[1] = (byte)(sumG / kernelWeight);
                    blurredPixel[2] = (byte)(sumR / kernelWeight);
                }
            }

            fastOriginal.Unlock();
            fastBlurred.Unlock();
        }

        return blurredBitmap;
    }
}