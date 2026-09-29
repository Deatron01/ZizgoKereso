import sys
import cv2
import numpy as np
import os
import shutil
import datetime
from PyQt6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout,
                             QHBoxLayout, QPushButton, QLabel, QScrollArea,
                             QFileDialog, QMessageBox, QFrame)
from PyQt6.QtGui import QPixmap, QImage, QFont
from PyQt6.QtCore import Qt, QTimer

# Segédfüggvény: OpenCV képből PyQt Pixmap konvertáló
def cv_to_pixmap(cv_img):
    # Biztosítjuk, hogy a memória egybefüggő legyen
    cv_img = np.ascontiguousarray(cv_img)
    
    if len(cv_img.shape) == 2:  # Fekete-fehér maszk
        h, w = cv_img.shape
        bytes_per_line = w
        # A .data helyett .tobytes() kell a PyQt6-nak!
        q_img = QImage(cv_img.tobytes(), w, h, bytes_per_line, QImage.Format.Format_Grayscale8)
    else:  # Színes kép (BGR)
        h, w, c = cv_img.shape
        bytes_per_line = 3 * w
        # A .data helyett .tobytes() kell a PyQt6-nak!
        q_img = QImage(cv_img.tobytes(), w, h, bytes_per_line, QImage.Format.Format_RGB888).rgbSwapped()
        
    return QPixmap.fromImage(q_img)

# ---------- ÚJ ABLAK: AZ ALGORITMUS MEGJELENÍTÉSE ----------
class ProcessViewerWindow(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("⚙️ Zizgő Kereső Algoritmus - ÉLŐ FOLYAMAT")
        self.resize(1000, 500)
        self.setStyleSheet("background-color: #1e1e1e; color: white;")

        layout = QVBoxLayout(self)

        # Információs szöveg
        self.info_label = QLabel("Keresés előkészítése...")
        self.info_label.setFont(QFont("Arial", 14, QFont.Weight.Bold))
        self.info_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(self.info_label)

        # Képek elrendezése egymás mellett
        images_layout = QHBoxLayout()
        
        # 1. Jelenleg vizsgált minta
        self.template_label = QLabel()
        self.template_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        images_layout.addWidget(self.template_label)

        # 2. Színmaszk
        self.mask_label = QLabel()
        self.mask_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        images_layout.addWidget(self.mask_label)

        # 3. Hőtérkép (Heatmap)
        self.heatmap_label = QLabel()
        self.heatmap_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        images_layout.addWidget(self.heatmap_label)

        layout.addLayout(images_layout)

    def update_view(self, text, template_pixmap, mask_pixmap, heatmap_pixmap):
        self.info_label.setText(text)
        self.template_label.setPixmap(template_pixmap.scaled(200, 200, Qt.AspectRatioMode.KeepAspectRatio))
        self.mask_label.setPixmap(mask_pixmap.scaled(350, 350, Qt.AspectRatioMode.KeepAspectRatio))
        self.heatmap_label.setPixmap(heatmap_pixmap.scaled(350, 350, Qt.AspectRatioMode.KeepAspectRatio))


# ---------- FŐ ALKALMAZÁS ----------
class ZizgoApp(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Zizgő Kereső")
        self.resize(1200, 800)
        self.setStyleSheet("background-color: #2b2b2b; color: white;")

        self.main_image_path = ""
        self.template_paths = []

        # Élő folyamat változói
        self.process_window = None
        self.timer = QTimer()
        self.timer.timeout.connect(self.process_next_template)
        
        self.current_template_index = 0
        self.best_score = -1
        self.best_rect = None
        self.best_template_path = None

        self.setup_ui()

    def setup_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        main_layout = QVBoxLayout(central_widget)

        # Felső rész: Kép és Minták
        top_layout = QHBoxLayout()
        main_layout.addLayout(top_layout, stretch=1)

        self.image_label = QLabel("Kérlek tölts be egy képet...")
        self.image_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.image_label.setStyleSheet("background-color: #1e1e1e; font-size: 16px; color: gray; border: 1px solid #444;")
        top_layout.addWidget(self.image_label, stretch=3) 

        right_frame = QFrame()
        right_frame.setStyleSheet("background-color: #404040; border-radius: 5px;")
        right_layout = QVBoxLayout(right_frame)

        title = QLabel("Zizgő Minták")
        title.setFont(QFont("Arial", 14, QFont.Weight.Bold))
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        right_layout.addWidget(title)

        self.scroll_area = QScrollArea()
        self.scroll_area.setWidgetResizable(True)
        self.scroll_area.setStyleSheet("border: none; background-color: #404040;")
        self.scroll_content = QWidget()
        self.scroll_content.setStyleSheet("background-color: #404040;")
        self.scroll_layout = QVBoxLayout(self.scroll_content)
        self.scroll_layout.setAlignment(Qt.AlignmentFlag.AlignTop)
        self.scroll_area.setWidget(self.scroll_content)
        right_layout.addWidget(self.scroll_area)

        top_layout.addWidget(right_frame, stretch=1) 

        # Alsó panel gombok
        controls_layout = QHBoxLayout()
        btn_style = """
            QPushButton {
                background-color: #3c3f41; border: 1px solid #555; border-radius: 4px; padding: 10px 15px; font-size: 14px;
            }
            QPushButton:hover { background-color: #4e5254; }
        """

        btn_load_main = QPushButton("1. Kép megnyitása")
        btn_load_main.setStyleSheet(btn_style)
        btn_load_main.clicked.connect(self.load_main_image)

        btn_load_temp = QPushButton("2. Minták betöltése")
        btn_load_temp.setStyleSheet(btn_style)
        btn_load_temp.clicked.connect(self.load_templates)

        btn_run = QPushButton("3. KERESÉS ÉLŐ FOLYAMATTAL")
        btn_run.setStyleSheet(btn_style + "color: #ff4444; font-weight: bold; font-size: 16px;")
        btn_run.clicked.connect(self.start_visual_detection)

        controls_layout.addWidget(btn_load_main)
        controls_layout.addWidget(btn_load_temp)
        controls_layout.addStretch() 
        controls_layout.addWidget(btn_run)
        main_layout.addLayout(controls_layout)

    def load_main_image(self):
        path, _ = QFileDialog.getOpenFileName(self, "Válassz egy képet", "", "Images (*.png *.jpg *.jpeg);;All Files (*)")
        if not path: return
        self.main_image_path = path
        pixmap = QPixmap(path)
        self.image_label.setPixmap(pixmap.scaled(800, 600, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))

    def load_templates(self):
        folder = QFileDialog.getExistingDirectory(self, "Válaszd ki a minták mappáját")
        if not folder: return
        while self.scroll_layout.count():
            child = self.scroll_layout.takeAt(0)
            if child.widget(): child.widget().deleteLater()
        self.template_paths = []
        files = [f for f in os.listdir(folder) if f.lower().endswith((".png", ".jpg", ".jpeg"))]
        for f in files:
            full_path = os.path.join(folder, f)
            self.template_paths.append(full_path)
            item_widget = QWidget()
            item_layout = QVBoxLayout(item_widget)
            item_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
            
            img_label = QLabel()
            img_label.setPixmap(QPixmap(full_path).scaled(120, 120, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))
            item_layout.addWidget(img_label)
            
            text_label = QLabel(f)
            text_label.setStyleSheet("font-size: 11px;")
            item_layout.addWidget(text_label)
            self.scroll_layout.addWidget(item_widget)

    # ----------------------------------------------------
    # ALGORITMUS ÉS VIZUALIZÁCIÓ LÉPÉSRŐL LÉPÉSRE
    # ----------------------------------------------------

    def start_visual_detection(self):
        if not self.main_image_path or not self.template_paths:
            QMessageBox.warning(self, "Hiba", "Töltsd be a képet és a mintákat!")
            return

        # Változók alaphelyzetbe állítása a folyamathoz
        self.img_full = cv2.imread(self.main_image_path)
        self.work_scale = self.img_full.shape[1] / 800.0
        self.work_w = 800
        self.work_h = int(self.img_full.shape[0] / self.work_scale)

        self.img_work = cv2.resize(self.img_full, (self.work_w, self.work_h))
        self.hsv_work = cv2.cvtColor(self.img_work, cv2.COLOR_BGR2HSV)

        self.current_template_index = 0
        self.best_score = -1
        self.best_rect = None
        self.best_template_path = None

        # Megnyitjuk a nézegető ablakot
        if self.process_window is None:
            self.process_window = ProcessViewerWindow()
        self.process_window.show()

        # Elindítjuk az időzítőt (500 ms = fél másodperc szünet minden képnél, hogy lásd mi történik)
        self.timer.start(500)

    def process_next_template(self):

        if self.current_template_index >= len(self.template_paths):
            self.timer.stop()
            self.process_window.hide()
            self.finalize_detection()
            return

        t_path = self.template_paths[self.current_template_index]
        filename = os.path.basename(t_path)
        
        template = cv2.imread(t_path, cv2.IMREAD_UNCHANGED)
        
        if template is not None:
            tw = int(template.shape[1] / self.work_scale)
            th = int(template.shape[0] / self.work_scale)

            if tw >= 10 and th >= 10:
                t_resized = cv2.resize(template, (tw, th))

                if t_resized.shape[2] == 4:
                    mask = t_resized[:, :, 3]
                    t_color = t_resized[:, :, :3]
                else:
                    t_color = t_resized
                    mask = np.ones((th, tw), dtype=np.uint8) * 255

                t_hsv = cv2.cvtColor(t_color, cv2.COLOR_BGR2HSV)
                valid_pixels = t_hsv[mask > 10]
                
                if len(valid_pixels) > 0:
                    median_h = np.median(valid_pixels[:, 0])
                    lower_bound = np.array([max(0, median_h - 25), 40, 30])
                    upper_bound = np.array([min(179, median_h + 25), 255, 255])

                    color_mask = cv2.inRange(self.hsv_work, lower_bound, upper_bound)
                    img_masked = cv2.bitwise_and(self.img_work, self.img_work, mask=color_mask)

                    # Template illesztés
                    res = cv2.matchTemplate(img_masked, t_color, cv2.TM_CCOEFF_NORMED, mask=mask)
                    
                    # ---- EZT A SORT ADD HOZZÁ A NaN HIBA JAVÍTÁSÁHOZ ----
                    # A nullával osztás miatt keletkező 'nan' és végtelen értékeket 0-ra cseréljük
                    res = np.nan_to_num(res, nan=0.0, posinf=0.0, neginf=0.0)
                    # -----------------------------------------------------

                    _, max_val, _, max_loc = cv2.minMaxLoc(res)

                    if max_val > self.best_score:
                        self.best_score = max_val
                        self.best_rect = (max_loc[0], max_loc[1], tw, th)
                        self.best_template_path = t_path
                    # --- VIZUALIZÁCIÓ LÉTREHOZÁSA AZ ABLAKNAK ---
                    # 1. Sablon kép
                    t_vis = cv_to_pixmap(t_color)
                    
                    # 2. Színmaszk (Mit lát a gép)
                    mask_vis = cv_to_pixmap(color_mask)
                    
                    # 3. Hőtérkép (Hol van a találat) - Normalizáljuk és rátesszük a piros-kék színtérképet
                    heatmap = cv2.normalize(res, None, 0, 255, cv2.NORM_MINMAX, dtype=cv2.CV_8U)
                    heatmap_color = cv2.applyColorMap(heatmap, cv2.COLORMAP_JET)
                    # Visszaméretezzük a kijelzőméretre, hogy ne egy apró pacát lássunk
                    heatmap_resized = cv2.resize(heatmap_color, (self.work_w, self.work_h))
                    heatmap_vis = cv_to_pixmap(heatmap_resized)

                    text = f"Keresés: {self.current_template_index + 1} / {len(self.template_paths)} | {filename}\nJelenlegi találat: {max_val*100:.1f}%"
                    self.process_window.update_view(text, t_vis, mask_vis, heatmap_vis)

        # Lépés a következő sablonra
        self.current_template_index += 1

    def finalize_detection(self):
        if self.best_score > 0.3 and self.best_rect:
            x, y, w, h = (
                int(self.best_rect[0] * self.work_scale),
                int(self.best_rect[1] * self.work_scale),
                int(self.best_rect[2] * self.work_scale),
                int(self.best_rect[3] * self.work_scale),
            )

            cv2.rectangle(self.img_full, (x, y), (x + w, y + h), (0, 0, 255), 20)
            timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
            save_dir = os.path.join(os.getcwd(), f"Zizgo_Eredmeny_{timestamp}")
            os.makedirs(save_dir, exist_ok=True)

            cv2.imwrite(os.path.join(save_dir, "Megtalalva_Kerettel.jpg"), self.img_full)
            shutil.copy(self.best_template_path, os.path.join(save_dir, "Legjobb_Sablon_Referencia.png"))

            self.show_result_window(self.img_full, self.best_score, save_dir)
        else:
            QMessageBox.information(self, "Eredmény", f"Sajnos Zizgő nincs meg a képen. ({self.best_score:.2f})")

    def show_result_window(self, cv_img, score, save_dir):
        pixmap = cv_to_pixmap(cv_img)
        self.res_win = QMainWindow(self)
        self.res_win.setWindowTitle("ZIZGŐ MEGTALÁLVA!")
        self.res_win.resize(900, 800)
        self.res_win.setStyleSheet("background-color: #2b2b2b; color: white;")

        central_widget = QWidget()
        self.res_win.setCentralWidget(central_widget)
        layout = QVBoxLayout(central_widget)

        title = QLabel(f"SIKER! Pontosság: {score*100:.1f}%\nMentesítve ide: {save_dir}")
        title.setFont(QFont("Arial", 14, QFont.Weight.Bold))
        title.setStyleSheet("color: #4caf50;")
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(title)

        img_label = QLabel()
        img_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        img_label.setPixmap(pixmap.scaled(850, 700, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation))
        layout.addWidget(img_label)

        self.res_win.show()

if __name__ == "__main__":
    app = QApplication(sys.argv)
    app.setAttribute(Qt.ApplicationAttribute.AA_DontUseNativeMenuBar)
    window = ZizgoApp()
    window.show()
    sys.exit(app.exec())