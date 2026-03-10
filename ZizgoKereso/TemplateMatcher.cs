using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

public class MatchResult
{
    public Point Location { get; set; }
    public double Score { get; set; }
    public double Scale { get; set; }
    public Rectangle BoundingBox { get; set; }
}

public class TemplateMatcher
{
    /// <summary>
     /// Képpiramis generálása: A keresési képet több szinten lekicsinyíti[cite: 18, 19].
    /// </summary>
    public static List<Bitmap> BuildImagePyramid(Bitmap original, int levels, double scaleFactor)
    {
        List<Bitmap> pyramid = new List<Bitmap>();
        pyramid.Add((Bitmap)original.Clone());

        for (int i = 1; i < levels; i++)
        {
            int newWidth = (int)(pyramid[i - 1].Width * scaleFactor);
            int newHeight = (int)(pyramid[i - 1].Height * scaleFactor);

            if (newWidth < 10 || newHeight < 10) break; // Túl kicsi képnél megállunk

            Bitmap scaled = new Bitmap(newWidth, newHeight);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                // Magas minőségű átméretezés
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(pyramid[i - 1], 0, 0, newWidth, newHeight);
            }
            pyramid.Add(scaled);
        }

        return pyramid;
    }

    /// <summary>
    /// SSD alapú keresés. Az első paraméter MÁR egy zárolt FastBitmap!
    /// </summary>
    public static unsafe MatchResult FindTemplateSSD(FastBitmap fastSearch, Bitmap template, double maxAllowedErrorPerPixel)
    {
        MatchResult bestMatch = new MatchResult { Score = double.MaxValue };

        // Itt már csak a sablont kell zárolni, mert a keresési kép (fastSearch) már nyitva van
        using (FastBitmap fastTemplate = new FastBitmap(template))
        {
            fastTemplate.Lock();

            int validTemplatePixels = CountValidPixels(fastTemplate);
            double maxTotalError = validTemplatePixels * maxAllowedErrorPerPixel;

            for (int y = 0; y <= fastSearch.Height - fastTemplate.Height; y++)
            {
                for (int x = 0; x <= fastSearch.Width - fastTemplate.Width; x++)
                {
                    double currentError = 0;
                    bool earlyExit = false;

                    for (int ty = 0; ty < fastTemplate.Height; ty++)
                    {
                        for (int tx = 0; tx < fastTemplate.Width; tx++)
                        {
                            byte* tPixel = fastTemplate.GetPixelPointer(tx, ty);

                            if (tPixel[3] > 0)
                            {
                                byte* sPixel = fastSearch.GetPixelPointer(x + tx, y + ty);

                                int diffB = sPixel[0] - tPixel[0];
                                int diffG = sPixel[1] - tPixel[1];
                                int diffR = sPixel[2] - tPixel[2];

                                currentError += (diffB * diffB) + (diffG * diffG) + (diffR * diffR);
                            }
                        }

                        if (currentError > bestMatch.Score || currentError > maxTotalError)
                        {
                            earlyExit = true;
                            break;
                        }
                    }

                    if (!earlyExit && currentError < bestMatch.Score)
                    {
                        bestMatch.Score = currentError;
                        bestMatch.Location = new Point(x, y);
                        bestMatch.BoundingBox = new Rectangle(x, y, fastTemplate.Width, fastTemplate.Height);
                    }
                }
            }
            fastTemplate.Unlock();
        }

        return bestMatch;
    }

    /// <summary>
    /// Segédfüggvény: Megszámolja a sablonban a tényleges, nem átlátszó pixeleket.
    /// </summary>
    private static unsafe int CountValidPixels(FastBitmap template)
    {
        int count = 0;
        for (int y = 0; y < template.Height; y++)
        {
            for (int x = 0; x < template.Width; x++)
            {
                byte* pixel = template.GetPixelPointer(x, y);
                if (pixel[3] > 0) count++; // Ha nem teljesen átlátszó
            }
        }
        return count;
    }
}