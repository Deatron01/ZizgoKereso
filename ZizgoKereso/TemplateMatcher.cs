using System;
using System.Drawing;

namespace ZizgoKereso
{
    public class TemplateMatcher
    {
        public static unsafe MatchResult FindTemplateSSD(FastBitmap fastSearch, Bitmap template, double currentBestScore)
        {
            MatchResult bestMatch = new MatchResult { Score = double.MaxValue };
            using (FastBitmap fastTemplate = new FastBitmap(template))
            {
                fastTemplate.Lock();
                // Step 8: Hatalmas ugrások a keresésben
                for (int y = 0; y <= fastSearch.Height - fastTemplate.Height; y += 8)
                {
                    for (int x = 0; x <= fastSearch.Width - fastTemplate.Width; x += 8)
                    {
                        double error = 0; int count = 0;
                        // Belül is minden 8. pixelt nézzük (8x8 = 64x gyorsulás!)
                        for (int ty = 0; ty < fastTemplate.Height; ty += 8)
                        {
                            for (int tx = 0; tx < fastTemplate.Width; tx += 8)
                            {
                                byte* tP = fastTemplate.GetPixelPointer(tx, ty);
                                if (tP[3] == 0 || (tP[0] + tP[1] + tP[2] < 30)) continue;
                                byte* sP = fastSearch.GetPixelPointer(x + tx, y + ty);
                                error += Math.Abs(sP[0] - tP[0]) + Math.Abs(sP[1] - tP[1]) + Math.Abs(sP[2] - tP[2]);
                                count++;
                            }
                        }
                        double avg = count > 0 ? error / count : double.MaxValue;
                        if (avg < currentBestScore)
                        {
                            currentBestScore = avg;
                            bestMatch.Score = avg;
                            bestMatch.Location = new Point(x, y);
                        }
                    }
                }
                fastTemplate.Unlock();
            }
            return bestMatch;
        }
        public static unsafe Color GetAverageTemplateColor(Bitmap template)
        {
            long r = 0, g = 0, b = 0, count = 0;
            using (FastBitmap ft = new FastBitmap(template))
            {
                ft.Lock();
                for (int y = 0; y < ft.Height; y++)
                {
                    for (int x = 0; x < ft.Width; x++)
                    {
                        byte* p = ft.GetPixelPointer(x, y);
                        // Csak a nem fekete és nem átlátszó pixelek számítanak
                        if (p[3] > 0 && (p[0] + p[1] + p[2] > 30))
                        {
                            b += p[0]; g += p[1]; r += p[2];
                            count++;
                        }
                    }
                }
                ft.Unlock();
            }
            return count > 0 ? Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count)) : Color.Gray;
        }
    }
}