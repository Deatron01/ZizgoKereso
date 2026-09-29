using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace ZizgoKereso
{
    public class MatchResult



    {



        public Point Location { get; set; }



        public double Score { get; set; }



        public double Scale { get; set; }



        public Rectangle BoundingBox { get; set; }



        public Point[] FencePoints { get; set; }



        public Color TargetColor { get; set; }



        public string BestTemplatePath { get; set; }



        public string CroppedImagePath { get; set; }



    }
    public class ZizgoFinder
    {
        public static unsafe MatchResult FindBestMatch(string searchImagePath, string templateDirectory)
        {
            // 1. ELŐKÉSZÍTÉS (Kiskép a gyors pásztázáshoz)
            Bitmap originalImg = new Bitmap(searchImagePath);
            float scale = (float)originalImg.Width / 500f;
            int wW = 500; int wH = (int)(originalImg.Height / scale);

            Bitmap searchImg = new Bitmap(wW, wH);
            using (Graphics g = Graphics.FromImage(searchImg))
            {
                g.InterpolationMode = InterpolationMode.Low;
                g.DrawImage(originalImg, 0, 0, wW, wH);
            }

            string[] templates = Directory.GetFiles(templateDirectory, "*.png").Take(15).ToArray();
            MatchResult globalBest = new MatchResult { Score = double.MaxValue };
            string bestTemplatePath = "";

            using (FastBitmap fastSearch = new FastBitmap(searchImg))
            {
                fastSearch.Lock();
                foreach (string tPath in templates)
                {
                    using (Bitmap rawT = new Bitmap(tPath))
                    {
                        Color target = TemplateMatcher.GetAverageTemplateColor(rawT);
                        int tw = (int)(rawT.Width / scale);
                        int th = (int)(rawT.Height / scale);

                        for (int y = 0; y <= fastSearch.Height - th; y += 10)
                        {
                            for (int x = 0; x <= fastSearch.Width - tw; x += 10)
                            {
                                byte* p = fastSearch.GetPixelPointer(x + tw / 2, y + th / 2);
                                int d = Math.Abs(p[0] - target.B) + Math.Abs(p[1] - target.G) + Math.Abs(p[2] - target.R);

                                if (d > 60) continue;

                                using (Bitmap scaledT = new Bitmap(rawT, tw, th))
                                {
                                    MatchResult res = TemplateMatcher.FindTemplateSSD(fastSearch, scaledT, globalBest.Score);
                                    if (res.Score < globalBest.Score)
                                    {
                                        globalBest = res;
                                        bestTemplatePath = tPath; // Elmentjük a legjobb sablont a finomításhoz
                                    }
                                }
                            }
                            if (globalBest.Score < 20) break;
                        }
                    }
                    if (globalBest.Score < 20) break;
                }
                fastSearch.Unlock();
            }

            // 2. PRECIZIÓS FÁZIS (A skálázási hiba kiküszöbölése)
            if (globalBest.Score != double.MaxValue)
            {
                // Kijelölünk egy kis területet az EREDETI 4K képen a talált pont körül
                int roughX = (int)(globalBest.Location.X * scale);
                int roughY = (int)(globalBest.Location.Y * scale);

                using (Bitmap rawT = new Bitmap(bestTemplatePath))
                {
                    // A doboz mérete az eredeti képen
                    int finalW = rawT.Width;
                    int finalH = rawT.Height;

                    // Kicsit eltoljuk a keretet, hogy a kisképen talált középpont stimmeljen
                    globalBest.BoundingBox = new Rectangle(roughX, roughY, finalW, finalH);

                    // Itt opcionálisan futtathatnál egy szűkített SSD-t step=1-el, 
                    // de a középpont-visszaszámolás általában már elég.
                }
            }

            searchImg.Dispose();
            return globalBest;
        }
        public static Bitmap DrawBoundingBox(Bitmap original, MatchResult match)
        {
            Bitmap resultImg = (Bitmap)original.Clone();
            if (match.Score != double.MaxValue)
            {
                using (Graphics g = Graphics.FromImage(resultImg))
                using (Pen redPen = new Pen(Color.Red, 30)) // Még vastagabb, hogy biztos látszódjon
                {
                    g.DrawRectangle(redPen, match.BoundingBox);
                }
            }
            return resultImg;
        }
        
    }
}