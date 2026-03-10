using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ZizgoKereso
{
    public partial class MainForm : Form
    {
        private string searchImagePath = "";
        private string templateDirectoryPath = "";
        private Bitmap currentImage = null;

        public MainForm()
        {
            InitializeComponent();
            lblStatus.Text = "Kérlek, tölts be egy tesztképet és a sablonok mappáját!";
        }

        // 1. Gomb: Keresési kép (tesztfotó) betöltése
        private void btnLoadImage_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Válassz egy tesztfotót";
                ofd.Filter = "Képfájlok (*.jpg;*.png;*.bmp)|*.jpg;*.png;*.bmp";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    searchImagePath = ofd.FileName;
                    if (currentImage != null) currentImage.Dispose();

                    currentImage = new Bitmap(searchImagePath);
                    pictureBoxResult.Image = currentImage;
                    pictureBoxResult.SizeMode = PictureBoxSizeMode.Zoom;

                    lblStatus.Text = "Kép betöltve: " + Path.GetFileName(searchImagePath);
                }
            }
        }

        // 2. Gomb: Sablonok mappájának betallózása (a Blenderes mappád)
        private void btnLoadTemplates_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Válaszd ki a generált sablonképek (PNG) mappáját";

                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    templateDirectoryPath = fbd.SelectedPath;
                    int fileCount = Directory.GetFiles(templateDirectoryPath, "*.png").Length;
                    lblStatus.Text = $"Sablon mappa betöltve. ({fileCount} db PNG sablon található)";
                }
            }
        }

        // 3. Gomb: Keresés indítása (Aszinkron futtatás!)
        private async void btnStartSearch_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(searchImagePath) || string.IsNullOrEmpty(templateDirectoryPath))
            {
                MessageBox.Show("Elõbb tölts be egy képet és a sablonok mappáját!", "Hiba", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnStartSearch.Enabled = false;
            lblStatus.Text = "Keresés folyamatban (124 sablon illesztése)... Kérlek várj!";

            // Futási idõ mérésének indítása a UI-on 
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                // A nehéz számítást kiszervezzük egy háttérszálra, hogy ne fagyjon ki a UI
                MatchResult result = await Task.Run(() =>
                {
                    return ZizgoFinder.FindBestMatch(searchImagePath, templateDirectoryPath);
                });

                sw.Stop();

                if (result.Score != double.MaxValue)
                {
                    // Vizuális visszajelzés (Bounding Box) rárajzolása a képre 
                    Bitmap resultBmp = ZizgoFinder.DrawBoundingBox(currentImage, result);
                    pictureBoxResult.Image = resultBmp;

                    lblStatus.Text = $"Zizgõ megtalálva! Futási idõ: {sw.ElapsedMilliseconds} ms. Hibafaktor: {result.Score}";
                }
                else
                {
                    lblStatus.Text = $"Zizgõ nem található a képen. (Futási idõ: {sw.ElapsedMilliseconds} ms)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Hiba történt a keresés során: " + ex.Message, "Kritikus hiba", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Hiba történt.";
            }
            finally
            {
                btnStartSearch.Enabled = true;
            }
        }
    }
}