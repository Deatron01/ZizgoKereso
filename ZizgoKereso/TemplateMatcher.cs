using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ZizgoKereso
{
    public struct TemplatePixelOffset
    {
        public int Offset;
        public byte B, G, R;
    }

    public class TemplateMatcher
    {
        public static List<Bitmap> BuildImagePyramid(Bitmap original, int levels, double scaleFactor)
        {
            List<Bitmap> pyramid = new List<Bitmap>();
            pyramid.Add((Bitmap)original.Clone());

            for (int i = 1; i < levels; i++)
            {
                int newWidth = (int)(pyramid[i - 1].Width * scaleFactor);
                int newHeight = (int)(pyramid[i - 1].Height * scaleFactor);

                if (newWidth < 40 || newHeight < 40) break;

                Bitmap scaled = new Bitmap(newWidth, newHeight);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(pyramid[i - 1], 0, 0, newWidth, newHeight);
                }
                pyramid.Add(scaled);
            }

            return pyramid;
        }

        public static unsafe MatchResult FindTemplateSSD(FastBitmap fastSearch, Bitmap template, double maxAllowedErrorPerPixel)
        {
            MatchResult bestMatch = new MatchResult { Score = double.MaxValue };

            using (FastBitmap fastTemplate = new FastBitmap(template))
            {
                fastTemplate.Lock();

                // 1. Sablon pixelek kigyűjtése (Csak a karakter színes részeit nézzük)
                List<TemplatePixelOffset> validPixelsList = new List<TemplatePixelOffset>();
                for (int ty = 0; ty < fastTemplate.Height; ty += 2)
                {
                    for (int tx = 0; tx < fastTemplate.Width; tx += 2)
                    {
                        byte* tP = fastTemplate.GetPixelPointer(tx, ty);
                        int max = Math.Max(tP[2], Math.Max(tP[1], tP[0]));
                        int min = Math.Min(tP[2], Math.Min(tP[1], tP[0]));

                        if (max - min > 15)
                        {
                            validPixelsList.Add(new TemplatePixelOffset
                            {
                                Offset = (ty * fastSearch.Stride) + (tx * fastSearch.BytesPerPixel),
                                B = tP[0],
                                G = tP[1],
                                R = tP[2]
                            });
                        }
                    }
                }

                if (validPixelsList.Count < 10) { fastTemplate.Unlock(); return bestMatch; }
                TemplatePixelOffset[] tPixels = validPixelsList.ToArray();

                // --- 2. DURVA KERESÉS (Nagy léptekkel a sebességért) ---
                int step = 6;
                double bestTotalError = double.MaxValue;

                for (int y = 0; y <= fastSearch.Height - fastTemplate.Height; y += step)
                {
                    for (int x = 0; x <= fastSearch.Width - fastTemplate.Width; x += step)
                    {
                        double currentError = CalculateError(fastSearch, x, y, tPixels, bestTotalError);
                        if (currentError < bestTotalError)
                        {
                            bestTotalError = currentError;
                            bestMatch.Location = new Point(x, y);
                        }
                    }
                }

                // --- 3. FINOMÍTÁS ÉS KERET TÁGÍTÁSA ---
                if (bestTotalError != double.MaxValue)
                {
                    // Precíz hely meghatározás a durva találat körül
                    int startX = Math.Max(0, bestMatch.Location.X - 8);
                    int startY = Math.Max(0, bestMatch.Location.Y - 8);
                    int endX = Math.Min(fastSearch.Width - fastTemplate.Width, bestMatch.Location.X + 8);
                    int endY = Math.Min(fastSearch.Height - fastTemplate.Height, bestMatch.Location.Y + 8);

                    for (int y = startY; y <= endY; y++)
                    {
                        for (int x = startX; x <= endX; x++)
                        {
                            double currentError = CalculateError(fastSearch, x, y, tPixels, bestTotalError);
                            if (currentError < bestTotalError)
                            {
                                bestTotalError = currentError;
                                bestMatch.Location = new Point(x, y);
                            }
                        }
                    }

                    bestMatch.Score = bestTotalError / tPixels.Length;

                    // --- DINAMIKUS TÁGÍTÁS ---
                    // Meghatározzuk a kezdő határokat a sablon alapján
                    int finalLeft = bestMatch.Location.X;
                    int finalTop = bestMatch.Location.Y;
                    int finalRight = finalLeft + fastTemplate.Width;
                    int finalBottom = finalTop + fastTemplate.Height;

                    bool changed = true;
                    while (changed)
                    {
                        changed = false;

                        // Balra tágítás: ha a bal szélen még van "Zizgő-szín"
                        if (finalLeft > 0 && IsEdgeRelevant(fastSearch, finalLeft - 1, finalTop, finalBottom, true))
                        {
                            finalLeft--;
                            changed = true;
                        }
                        // Jobbra tágítás
                        if (finalRight < fastSearch.Width - 1 && IsEdgeRelevant(fastSearch, finalRight + 1, finalTop, finalBottom, true))
                        {
                            finalRight++;
                            changed = true;
                        }
                        // Felfelé tágítás
                        if (finalTop > 0 && IsEdgeRelevant(fastSearch, finalTop - 1, finalLeft, finalRight, false))
                        {
                            finalTop--;
                            changed = true;
                        }
                        // Lefelé tágítás
                        if (finalBottom < fastSearch.Height - 1 && IsEdgeRelevant(fastSearch, finalBottom + 1, finalLeft, finalRight, false))
                        {
                            finalBottom++;
                            changed = true;
                        }
                    }

                    // A végső négyszög, ami már teljesen tartalmazza Zizgőt
                    bestMatch.BoundingBox = new Rectangle(finalLeft, finalTop, finalRight - finalLeft, finalBottom - finalTop);
                }

                fastTemplate.Unlock();
            }
            return bestMatch;
        }

        // Segédfüggvény: Megnézi, hogy egy adott pixelsor/oszlop tartalmaz-e Zizgőhöz tartozó színt
        private static unsafe bool IsEdgeRelevant(FastBitmap img, int coord, int start, int end, bool isVertical)
        {
            int matchCount = 0;
            for (int i = start; i < end; i++)
            {
                byte* p = isVertical ? img.GetPixelPointer(coord, i) : img.GetPixelPointer(i, coord);

                // Zizgő jellegzetes kékes-zöld színe (B: ~160, G: ~200, R: ~100 környéke)
                // A telítettséget nézzük (max-min > 15), hogy ne a szürke hátteret találja meg
                int max = Math.Max(p[2], Math.Max(p[1], p[0]));
                int min = Math.Min(p[2], Math.Min(p[1], p[0]));

                if (max - min > 15 && p[1] > 100) // Ha van színe és a zöld csatorna erős
                {
                    matchCount++;
                }
            }

            // Ha a vizsgált él legalább 10%-a releváns színű, akkor tágítunk tovább
            return matchCount > (end - start) * 0.1;
        }

        // Segédfüggvény a hiba kiszámításához (hogy ne ismételjük a kódot)
        private static unsafe double CalculateError(FastBitmap fastSearch, int x, int y, TemplatePixelOffset[] tPixels, double currentBest)
        {
            double error = 0;
            byte* windowBase = fastSearch.GetPixelPointer(x, y);

            for (int i = 0; i < tPixels.Length; i++)
            {
                byte* sPixel = windowBase + tPixels[i].Offset;
                int dB = sPixel[0] - tPixels[i].B;
                int dG = sPixel[1] - tPixels[i].G;
                int dR = sPixel[2] - tPixels[i].R;
                error += (dB * dB) + (dG * dG) + (dR * dR);

                if (error > currentBest) return double.MaxValue; // Early Exit
            }
            return error;
        }
    }
}