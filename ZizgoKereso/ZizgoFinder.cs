using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ZizgoKereso
{
    public class MatchResult
    {
        public Point Location { get; set; }
        public double Score { get; set; }
        public double Scale { get; set; }
        public Rectangle BoundingBox { get; set; }
        public string BestTemplatePath { get; set; } // Megjegyzi, melyik kép nyert
        public string CroppedImagePath { get; set; } // A kivágott eredmény helye
    }

    public class TemplateLogEntry
    {
        public string SablonNeve { get; set; }
        public double Hibapontszam { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public double Skalazas { get; set; }
    }

    public class ZizgoFinder
    {
        private static string currentOutputDirectory = "";

        public static MatchResult FindBestMatch(string searchImagePath, string templateDirectory)
        {
            string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            currentOutputDirectory = Path.Combine(desktopPath, "Zizgo_Eredmenyek", $"Futas_{runId}");
            Directory.CreateDirectory(currentOutputDirectory);

            string originalCopyPath = Path.Combine(currentOutputDirectory, "1_Eredeti_Keresesi_Kep" + Path.GetExtension(searchImagePath));
            File.Copy(searchImagePath, originalCopyPath, true);

            Stopwatch stopwatch = Stopwatch.StartNew();
            Process currentProcess = Process.GetCurrentProcess();
            long initialMemory = currentProcess.WorkingSet64;

            Bitmap tempOriginal = new Bitmap(searchImagePath);
            Bitmap processedSearchImage;

            if (tempOriginal.Width > 1200)
            {
                int newWidth = 1200;
                int newHeight = (int)(tempOriginal.Height * ((float)newWidth / tempOriginal.Width));
                processedSearchImage = new Bitmap(tempOriginal, new Size(newWidth, newHeight));
                tempOriginal.Dispose();
            }
            else
            {
                processedSearchImage = tempOriginal;
            }

            Bitmap blurredSearchImage = ImageProcessor.ApplyGaussianBlur(processedSearchImage);
            blurredSearchImage.Save(Path.Combine(currentOutputDirectory, "2_Elofeldolgozott_Kep.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);

            string[] templateFiles = Directory.GetFiles(templateDirectory, "*.*").Where(s => s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)).ToArray();

            MatchResult globalBestMatch = new MatchResult { Score = double.MaxValue };
            object lockObject = new object();
            ConcurrentBag<TemplateLogEntry> searchLogs = new ConcurrentBag<TemplateLogEntry>();

            using (FastBitmap fastSearch = new FastBitmap(blurredSearchImage))
            {
                fastSearch.Lock();

                Parallel.ForEach(templateFiles, (templatePath) =>
                {
                    MatchResult bestForThisTemplate = new MatchResult { Score = double.MaxValue };
                    string templateName = Path.GetFileName(templatePath);

                    using (Bitmap originalTemplate = new Bitmap(templatePath))
                    {
                        Bitmap template;
                        if (originalTemplate.Width > 150)
                            template = new Bitmap(originalTemplate, new Size(150, 150));
                        else
                            template = new Bitmap(originalTemplate);

                        // LASSABB, DE SOKKAL PONTOSABB PIRAMIS: 8 szint, 0.85-ös ugrásokkal!
                        List<Bitmap> pyramid = TemplateMatcher.BuildImagePyramid(template, 8, 0.85);

                        foreach (Bitmap scaledTemplate in pyramid)
                        {
                            if (scaledTemplate.Width > fastSearch.Width || scaledTemplate.Height > fastSearch.Height)
                                continue;

                            MatchResult localResult = TemplateMatcher.FindTemplateSSD(fastSearch, scaledTemplate, 4500.0);

                            if (localResult.Score < bestForThisTemplate.Score)
                            {
                                bestForThisTemplate = localResult;
                                bestForThisTemplate.Scale = ((double)scaledTemplate.Width / template.Width) * ((double)template.Width / originalTemplate.Width);
                            }

                            lock (lockObject)
                            {
                                if (localResult.Score < globalBestMatch.Score)
                                {
                                    globalBestMatch.Score = localResult.Score;
                                    globalBestMatch.Location = localResult.Location;
                                    globalBestMatch.BoundingBox = new Rectangle(localResult.Location.X, localResult.Location.Y, scaledTemplate.Width, scaledTemplate.Height);
                                    globalBestMatch.Scale = bestForThisTemplate.Scale;
                                    globalBestMatch.BestTemplatePath = templatePath; // Megjegyezzük a nyertest!
                                }
                            }
                        }

                        foreach (var bmp in pyramid) bmp.Dispose();
                        if (originalTemplate.Width > 150) template.Dispose();
                    }

                    if (bestForThisTemplate.Score != double.MaxValue)
                    {
                        searchLogs.Add(new TemplateLogEntry { SablonNeve = templateName, Hibapontszam = Math.Round(bestForThisTemplate.Score, 2), X = bestForThisTemplate.Location.X, Y = bestForThisTemplate.Location.Y, Skalazas = Math.Round(bestForThisTemplate.Scale, 2) });
                    }
                });
                fastSearch.Unlock();
            }

            stopwatch.Stop();

            currentProcess.Refresh();
            long memoryUsedMb = (currentProcess.WorkingSet64 - initialMemory) / (1024 * 1024);

            ExportSearchResults(searchLogs, runId);
            ExportPerformanceLog(stopwatch.ElapsedMilliseconds, memoryUsedMb, runId, searchImagePath, templateFiles.Length);

            if (globalBestMatch.Score != double.MaxValue)
            {
                SaveResultImage(processedSearchImage, globalBestMatch, runId);
                globalBestMatch.CroppedImagePath = SaveCroppedResult(processedSearchImage, globalBestMatch.BoundingBox, runId);

                if (!string.IsNullOrEmpty(globalBestMatch.BestTemplatePath))
                {
                    string winnerName = Path.GetFileName(globalBestMatch.BestTemplatePath);
                    // 1. Lementjük az eredeti győztes sablont
                    File.Copy(globalBestMatch.BestTemplatePath, Path.Combine(currentOutputDirectory, $"6_Gyoztes_Eredeti_Sablon_{winnerName}"), true);

                    // 2. Lementjük a pontosan lekicsinyített (transzformált) sablont, amivel egyezett!
                    using (Bitmap origTemp = new Bitmap(globalBestMatch.BestTemplatePath))
                    {
                        int scaledWidth = (int)(origTemp.Width * globalBestMatch.Scale);
                        int scaledHeight = (int)(origTemp.Height * globalBestMatch.Scale);

                        using (Bitmap scaledTemp = new Bitmap(scaledWidth, scaledHeight))
                        {
                            using (Graphics g = Graphics.FromImage(scaledTemp))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(origTemp, 0, 0, scaledWidth, scaledHeight);
                            }
                            scaledTemp.Save(Path.Combine(currentOutputDirectory, $"7_Gyoztes_Transzformalt_Sablon.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
            }

            return globalBestMatch;
        }

        private static string SaveCroppedResult(Bitmap sourceImage, Rectangle bbox, string runId)
        {
            int x = Math.Max(0, bbox.X);
            int y = Math.Max(0, bbox.Y);
            int width = Math.Min(sourceImage.Width - x, bbox.Width);
            int height = Math.Min(sourceImage.Height - y, bbox.Height);

            Rectangle cropRect = new Rectangle(x, y, width, height);
            string path = Path.Combine(currentOutputDirectory, $"5_Kivagott_Talalat_{runId}.jpg");

            using (Bitmap target = new Bitmap(cropRect.Width, cropRect.Height))
            {
                using (Graphics g = Graphics.FromImage(target))
                {
                    g.DrawImage(sourceImage, new Rectangle(0, 0, target.Width, target.Height), cropRect, GraphicsUnit.Pixel);
                }
                target.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            return path;
        }

        private static void ExportSearchResults(ConcurrentBag<TemplateLogEntry> logs, string runId)
        {
            string filePath = Path.Combine(currentOutputDirectory, $"3_Kereses_Reszletek_{runId}.csv");
            var sortedLogs = logs.OrderBy(l => l.Hibapontszam).ToList();

            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine("SablonNeve;Hibapontszam;X;Y;Skalazas");
                foreach (var log in sortedLogs)
                {
                    writer.WriteLine($"{log.SablonNeve};{log.Hibapontszam};{log.X};{log.Y};{log.Skalazas}");
                }
            }
        }

        private static void ExportPerformanceLog(long timeMs, long memoryMb, string runId, string imagePath, int templateCount)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, "Zizgo_Eredmenyek", "Teljesitmeny_Osszesito.csv");
            bool isNewFile = !File.Exists(filePath);

            using (StreamWriter writer = new StreamWriter(filePath, append: true))
            {
                if (isNewFile) writer.WriteLine("Futas_Azonosito;Tesztkep;Sablonok_Szama;Futasi_Ido_ms;Extra_Memoriahasznalat_MB;Datum");
                writer.WriteLine($"{runId};{Path.GetFileName(imagePath)};{templateCount};{timeMs};{memoryMb};{DateTime.Now}");
            }
        }

        private static void SaveResultImage(Bitmap processedImage, MatchResult bestMatch, string runId)
        {
            string imagePath = Path.Combine(currentOutputDirectory, $"4_Vegeleges_Eredmeny_{runId}.jpg");
            Bitmap finalImage = DrawBoundingBox(processedImage, bestMatch);
            finalImage.Save(imagePath, System.Drawing.Imaging.ImageFormat.Jpeg);
            finalImage.Dispose();
        }

        public static Bitmap DrawBoundingBox(Bitmap image, MatchResult match)
        {
            Bitmap resultImage = (Bitmap)image.Clone();
            using (Graphics g = Graphics.FromImage(resultImage))
            {
                Pen pen = new Pen(Color.LimeGreen, 5);
                g.DrawRectangle(pen, match.BoundingBox);
            }
            return resultImage;
        }
    }
}