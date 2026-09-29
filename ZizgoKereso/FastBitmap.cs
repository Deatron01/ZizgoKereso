using System;
using System.Drawing;
using System.Drawing.Imaging;

public unsafe class FastBitmap : IDisposable
{
    private Bitmap _bitmap;
    private BitmapData _bitmapData;
    private byte* _basePointer;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public int BytesPerPixel { get; private set; }

    public FastBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap;
        Width = bitmap.Width;
        Height = bitmap.Height;
    }

    // Memória zárolása a gyors olvasáshoz/íráshoz
    public void Lock()
    {
        Rectangle rect = new Rectangle(0, 0, Width, Height);
        _bitmapData = _bitmap.LockBits(rect, ImageLockMode.ReadWrite, _bitmap.PixelFormat);

        // A memóriaterület kezdőcíme
        _basePointer = (byte*)_bitmapData.Scan0.ToPointer();

        // Egy sor hossza bájtban (a memóriában lehet padding, ezért fontos a Stride!)
        Stride = _bitmapData.Stride;

        // Kiszámoljuk, hány bájt egy pixel (pl. 24 bites RGB = 3 bájt, 32 bites RGBA = 4 bájt)
        BytesPerPixel = Image.GetPixelFormatSize(_bitmap.PixelFormat) / 8;
    }

    // Memória feloldása
    public void Unlock()
    {
        if (_bitmapData != null)
        {
            _bitmap.UnlockBits(_bitmapData);
            _bitmapData = null;
            _basePointer = null;
        }
    }

    // Villámgyors pixel lekérés (Csak Lock() után használható!)
    public byte* GetPixelPointer(int x, int y)
    {
        // Kiszámoljuk az adott pixel pontos memóriacímét
        return _basePointer + (y * Stride) + (x * BytesPerPixel);
    }

    public void Dispose()
    {
        Unlock();
    }
}