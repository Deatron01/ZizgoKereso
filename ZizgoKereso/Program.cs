using System;
using System.Windows.Forms;

namespace ZizgoKereso
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            // Itt Form1 helyett MainForm-ot indítunk:
            Application.Run(new MainForm());
        }
    }
}