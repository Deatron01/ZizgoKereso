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

                // 1. Sablon elemzése: Pixelek kigyűjtése és ÁTLAGOS SZÍN számítása
                List<TemplatePixelOffset> validPixelsList = new List<TemplatePixelOffset>();
                long sumR = 0, sumG = 0, sumB = 0;

                for (int ty = 0; ty < fastTemplate.Height; ty += 2)
                {
                    for (int tx = 0; tx < fastTemplate.Width; tx += 2)
                    {
                        byte* tP = fastTemplate.GetPixelPointer(tx, ty);

                        // Csak a színes (nem szürke/fekete) részeket vesszük a cél-szín meghatározásához
                        int tMax = Math.Max(tP[2], Math.Max(tP[1], tP[0]));
                        int tMin = Math.Min(tP[2], Math.Min(tP[1], tP[0]));

                        if (tMax - tMin > 15)
                        {
                            validPixelsList.Add(new TemplatePixelOffset
                            {
                                Offset = (ty * fastSearch.Stride) + (tx * fastSearch.BytesPerPixel),
                                B = tP[0],
                                G = tP[1],
                                R = tP[2]
                            });
                            sumB += tP[0]; sumG += tP[1]; sumR += tP[2];
                        }
                    }
                }

                if (validPixelsList.Count < 10) { fastTemplate.Unlock(); return bestMatch; }

                // Dinamikus cél-szín rögzítése a sablon alapján (Zizgő aktuális árnyalata)
                bestMatch.TargetColor = Color.FromArgb(
                    (int)(sumR / validPixelsList.Count),
                    (int)(sumG / validPixelsList.Count),
                    (int)(sumB / validPixelsList.Count)
                );

                TemplatePixelOffset[] tPixels = validPixelsList.ToArray();

                // --- 2. DURVA KERESÉS (Szín-büntetéssel a kanapé ellen) ---
                int step = 6;
                double bestTotalError = double.MaxValue;

                for (int y = 0; y <= fastSearch.Height - fastTemplate.Height; y += step)
                {
                    for (int x = 0; x <= fastSearch.Width - fastTemplate.Width; x += step)
                    {
                        // Alap SSD hiba kiszámítása
                        double currentError = CalculateError(fastSearch, x, y, tPixels, double.MaxValue);

                        // KANAPÉ ELLENI VÉDELEM: 
                        // Megnézzük a vizsgált ablak közepét. Ha a színe nagyon eltér Zizgőétől, 
                        // és nem is semleges (szem/száj), akkor drasztikusan büntetjük a pontszámot.
                        byte* pMid = fastSearch.GetPixelPointer(x + fastTemplate.Width / 2, y + fastTemplate.Height / 2);
                        int dR = pMid[2] - bestMatch.TargetColor.R;
                        int dG = pMid[1] - bestMatch.TargetColor.G;
                        int dB = pMid[0] - bestMatch.TargetColor.B;
                        double colorDist = Math.Sqrt(dR * dR + dG * dG + dB * dB);

                        int midSat = Math.Max(pMid[2], Math.Max(pMid[1], pMid[0])) - Math.Min(pMid[2], Math.Min(pMid[1], pMid[0]));

                        // Ha rossz a szín ÉS nem szem/száj (van telítettsége), akkor büntetünk
                        if (colorDist > 65 && midSat > 20)
                        {
                            currentError *= 10.0;
                        }

                        if (currentError < bestTotalError)
                        {
                            bestTotalError = currentError;
                            bestMatch.Location = new Point(x, y);
                        }
                    }
                }

                // --- 3. FINOMÍTÁS ---
                if (bestTotalError != double.MaxValue)
                {
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

                    // --- 4. DINAMIKUS TÁGÍTÁS ---
                    int finalLeft = bestMatch.Location.X;
                    int finalTop = bestMatch.Location.Y;
                    int finalRight = finalLeft + fastTemplate.Width;
                    int finalBottom = finalTop + fastTemplate.Height;

                    int maxWidth = (int)(fastTemplate.Width * 2.5);
                    int maxHeight = (int)(fastTemplate.Height * 2.5);
                    bool changed = true;
                    int iter = 0;

                    while (changed && iter++ < 1000)
                    {
                        changed = false;
                        if (finalLeft > 0 && (finalRight - finalLeft) < maxWidth && IsEdgeRelevant(fastSearch, finalLeft - 1, finalTop, finalBottom, true, bestMatch.TargetColor))
                        { finalLeft--; changed = true; }
                        if (finalRight < fastSearch.Width - 1 && (finalRight - finalLeft) < maxWidth && IsEdgeRelevant(fastSearch, finalRight + 1, finalTop, finalBottom, true, bestMatch.TargetColor))
                        { finalRight++; changed = true; }
                        if (finalTop > 0 && (finalBottom - finalTop) < maxHeight && IsEdgeRelevant(fastSearch, finalTop - 1, finalLeft, finalRight, false, bestMatch.TargetColor))
                        { finalTop--; changed = true; }
                        if (finalBottom < fastSearch.Height - 1 && (finalBottom - finalTop) < maxHeight && IsEdgeRelevant(fastSearch, finalBottom + 1, finalLeft, finalRight, false, bestMatch.TargetColor))
                        { finalBottom++; changed = true; }
                    }

                    // Végleges befoglaló téglalap rögzítése
                    bestMatch.BoundingBox = new Rectangle(finalLeft, finalTop, finalRight - finalLeft, finalBottom - finalTop);

                    // 5. KERÍTÉS (Fence) GENERÁLÁSA a dinamikus középpontból
                    Point center = new Point(
                        bestMatch.BoundingBox.X + bestMatch.BoundingBox.Width / 2,
                        bestMatch.BoundingBox.Y + bestMatch.BoundingBox.Height / 2
                    );

                    bestMatch.FencePoints = GenerateZizgoFence(fastSearch, center, bestMatch.BoundingBox.Width, bestMatch.BoundingBox.Height, bestMatch.TargetColor);
                }

                fastTemplate.Unlock();
            }
            return bestMatch;
        }
        public static unsafe Point[] GenerateZizgoFence(FastBitmap img, Point center, int width, int height, Color targetColor)
        {
            int numRays = 72;
            Point[] points = new Point[numRays];
            double[] radii = new double[numRays];
            int maxRadius = (int)(Math.Max(width, height) * 1.5);

            for (int i = 0; i < numRays; i++)
            {
                double rad = (i * 5) * Math.PI / 180.0;
                double dx = Math.Cos(rad), dy = Math.Sin(rad);
                double bestR = 0;
                int gap = 0;

                for (int r = 5; r < maxRadius; r++)
                {
                    int cx = center.X + (int)(dx * r), cy = center.Y + (int)(dy * r);
                    if (cx < 0 || cx >= img.Width || cy < 0 || cy >= img.Height) break;

                    byte* p = img.GetPixelPointer(cx, cy);
                    int dR = p[2] - targetColor.R, dG = p[1] - targetColor.G, dB = p[0] - targetColor.B;
                    double dist = Math.Sqrt(dR * dR + dG * dG + dB * dB);

                    // ARC LOGIKA: Ha a pixel zöld, VAGY ha semleges (szem/száj: telítettség < 15)
                    int sat = Math.Max(p[2], Math.Max(p[1], p[0])) - Math.Min(p[2], Math.Min(p[1], p[0]));
                    bool isPlushColor = (dist < 55) || (sat < 15 && r < maxRadius * 0.6);

                    if (isPlushColor)
                    {
                        bestR = r;
                        gap = 0;
                    }
                    else if (++gap > 30) break;
                }
                radii[i] = bestR;
            }

            // 2. Simítás (Moving Average) tüskék ellen
            for (int i = 0; i < numRays; i++)
            {
                double sum = 0; int count = 0;
                for (int j = -2; j <= 2; j++)
                {
                    int idx = (i + j + numRays) % numRays;
                    if (radii[idx] > 0) { sum += radii[idx]; count++; }
                }
                double finalR = (count > 0 ? sum / count : 0) + 5;
                double rad = (i * (360.0 / numRays)) * Math.PI / 180.0;
                points[i] = new Point(center.X + (int)(Math.Cos(rad) * finalR), center.Y + (int)(Math.Sin(rad) * finalR));
            }
            return points;
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

        // TemplateMatcher.cs - Új metódus a kerítés pontjainak meghatározásához
        private static unsafe bool IsEdgeRelevant(FastBitmap img, int coord, int start, int end, bool isVertical, Color targetColor)
        {
            int matchCount = 0;
            for (int i = start; i < end; i++)
            {
                byte* p = isVertical ? img.GetPixelPointer(coord, i) : img.GetPixelPointer(i, coord);
                int dR = p[2] - targetColor.R, dG = p[1] - targetColor.G, dB = p[0] - targetColor.B;
                double dist = Math.Sqrt(dR * dR + dG * dG + dB * dB);

                if (dist < 50) matchCount++;
            }
            return matchCount > (end - start) * 0.12;
        }
    }
}