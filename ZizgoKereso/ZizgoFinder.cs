using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;

namespace ZizgoKereso
{
    public class MatchResult
    {
        public Point Location { get; set; }
        public double Score { get; set; }
        public double Scale { get; set; }
        public Rectangle BoundingBox { get; set; }
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
        // A fő mappa, ahová a logokat és képeket mentjük
        private static string outputDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Keresesi_Eredmenyek");

        public static MatchResult FindBestMatch(string searchImagePath, string templateDirectory)
        {
            // Biztosítjuk, hogy létezzen a kimeneti mappa
            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            // Egyedi azonosító a futásnak (pl. dátum_idő)
            string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            Stopwatch stopwatch = Stopwatch.StartNew();
            Process currentProcess = Process.GetCurrentProcess();
            long initialMemory = currentProcess.WorkingSet64; // Induláskori memória RAM-ban

            Bitmap originalSearchImage = new Bitmap(searchImagePath);
            Bitmap processedSearchImage = ImageProcessor.ApplyGaussianBlur(originalSearchImage);

            string[] templateFiles = Directory.GetFiles(templateDirectory, "*.png");
            MatchResult globalBestMatch = new MatchResult { Score = double.MaxValue };
            object lockObject = new object();

            // Szálbiztos lista a 124 sablon eredményének gyűjtésére
            ConcurrentBag<TemplateLogEntry> searchLogs = new ConcurrentBag<TemplateLogEntry>();

            using (FastBitmap fastSearch = new FastBitmap(processedSearchImage))
            {
                fastSearch.Lock();

                Parallel.ForEach(templateFiles, (templatePath) =>
                {
                    // Helyi (adott sablonhoz tartozó) legjobb eredmény
                    MatchResult bestForThisTemplate = new MatchResult { Score = double.MaxValue };
                    string templateName = Path.GetFileName(templatePath);

                    using (Bitmap template = new Bitmap(templatePath))
                    {
                        List<Bitmap> pyramid = TemplateMatcher.BuildImagePyramid(template, 3, 0.8);

                        foreach (Bitmap scaledTemplate in pyramid)
                        {
                            MatchResult localResult = TemplateMatcher.FindTemplateSSD(fastSearch, scaledTemplate, 3500.0);

                            // Frissítjük a sablon saját legjobbját
                            if (localResult.Score < bestForThisTemplate.Score)
                            {
                                bestForThisTemplate = localResult;
                                bestForThisTemplate.Scale = (double)scaledTemplate.Width / template.Width;
                            }

                            // Frissítjük a globális legjobbat (amit a program majd kirajzol)
                            lock (lockObject)
                            {
                                if (localResult.Score < globalBestMatch.Score)
                                {
                                    globalBestMatch.Score = localResult.Score;
                                    globalBestMatch.Location = localResult.Location;
                                    globalBestMatch.BoundingBox = localResult.BoundingBox;
                                    globalBestMatch.Scale = (double)scaledTemplate.Width / template.Width;
                                }
                            }
                        }
                        foreach (var bmp in pyramid) bmp.Dispose();
                    }

                    // Eredmény elmentése a szálbiztos listába
                    if (bestForThisTemplate.Score != double.MaxValue)
                    {
                        searchLogs.Add(new TemplateLogEntry
                        {
                            SablonNeve = templateName,
                            Hibapontszam = Math.Round(bestForThisTemplate.Score, 2),
                            X = bestForThisTemplate.Location.X,
                            Y = bestForThisTemplate.Location.Y,
                            Skalazas = Math.Round(bestForThisTemplate.Scale, 2)
                        });
                    }
                });

                fastSearch.Unlock();
            }

            stopwatch.Stop();

            // --- ADATOK EXPORTÁLÁSA ---
            currentProcess.Refresh();
            long finalMemory = currentProcess.WorkingSet64;
            long memoryUsedMb = (finalMemory - initialMemory) / (1024 * 1024); // Megabyte-ba váltás

            ExportSearchResults(searchLogs, runId);
            ExportPerformanceLog(stopwatch.ElapsedMilliseconds, memoryUsedMb, runId, searchImagePath, templateFiles.Length);

            // Ha talált valamit, lementjük a kész képet is a kerettel!
            if (globalBestMatch.Score != double.MaxValue)
            {
                SaveResultImage(originalSearchImage, globalBestMatch, runId);
            }

            return globalBestMatch;
        }

        // -- SEGÉDFÜGGVÉNYEK AZ EXPORTÁLÁSHOZ --

        private static void ExportSearchResults(ConcurrentBag<TemplateLogEntry> logs, string runId)
        {
            string filePath = Path.Combine(outputDirectory, $"Kereses_Reszletek_{runId}.csv");

            // Sorba rendezzük a legkisebb hibapontszám (legjobb illeszkedés) alapján
            var sortedLogs = logs.OrderBy(l => l.Hibapontszam).ToList();

            using (StreamWriter writer = new StreamWriter(filePath))
            {
                writer.WriteLine("Sablon Neve;Hiba (SSD Score);Talalat_X;Talalat_Y;Skalazas");
                foreach (var log in sortedLogs)
                {
                    writer.WriteLine($"{log.SablonNeve};{log.Hibapontszam};{log.X};{log.Y};{log.Skalazas}");
                }
            }
        }

        private static void ExportPerformanceLog(long timeMs, long memoryMb, string runId, string imagePath, int templateCount)
        {
            string filePath = Path.Combine(outputDirectory, "Teljesitmeny_Naplo.csv");
            bool isNewFile = !File.Exists(filePath);

            using (StreamWriter writer = new StreamWriter(filePath, append: true))
            {
                if (isNewFile)
                {
                    writer.WriteLine("Futas_Azonosito;Tesztkep;Sablonok_Szama;Futasi_Ido_ms;Extra_Memoriahasznalat_MB;Datum");
                }
                writer.WriteLine($"{runId};{Path.GetFileName(imagePath)};{templateCount};{timeMs};{memoryMb};{DateTime.Now}");
            }
        }

        private static void SaveResultImage(Bitmap originalImage, MatchResult bestMatch, string runId)
        {
            string imagePath = Path.Combine(outputDirectory, $"Eredmeny_Kep_{runId}.jpg");
            Bitmap finalImage = DrawBoundingBox(originalImage, bestMatch);
            finalImage.Save(imagePath, System.Drawing.Imaging.ImageFormat.Jpeg);
            finalImage.Dispose();
        }

        public static Bitmap DrawBoundingBox(Bitmap originalImage, MatchResult match)
        {
            Bitmap resultImage = (Bitmap)originalImage.Clone();
            using (Graphics g = Graphics.FromImage(resultImage))
            {
                Pen pen = new Pen(Color.LimeGreen, 5); // Szép, vastag zöld keret
                g.DrawRectangle(pen, match.BoundingBox);
            }
            return resultImage;
        }
    }
}