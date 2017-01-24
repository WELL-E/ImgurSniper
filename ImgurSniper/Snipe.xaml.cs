﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using Clipboard = System.Windows.Clipboard;

namespace ImgurSniper {
    /// <summary>
    /// Interaction logic for Snipe.xaml
    /// </summary>
    public partial class Snipe : Window {
        private string _dir;
        private ImgurIO _imgur;
        private NotifyIcon _nicon;


        public Snipe() {
            this.ShowActivated = false;
            InitializeComponent();

            Initialize();

            Position();

            this.Loaded += async delegate {
                //Prevent short flash of Toasts
                await Task.Delay(500);
                ErrorToast.Visibility = Visibility.Visible;
                SuccessToast.Visibility = Visibility.Visible;
            };

            Start();
        }

        private void Start() {
            string[] args = Environment.GetCommandLineArgs();
            bool instantUpload = false, autostart = false;
            string image = null;

            foreach(string arg in args) {
                if(arg.ToLower().Contains("autostart")) {
                    autostart = true;
                    break;
                }

                if(arg.ToLower().Contains("upload"))
                    instantUpload = true;

                if(File.Exists(arg))
                    image = arg;
            }

            if(autostart) {
                InitializeTray();
            } else if(instantUpload && image != null) {
                InstantUpload(image);
            } else {
                Crop(true);
            }
        }

        private void InitializeTray() {
            this.Visibility = Visibility.Collapsed;

            Key sKey = FileIO.ShortcutKey;
            bool usePrint = FileIO.UsePrint;

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Settings", delegate {
                try {
                    Process.Start(FileIO._programFiles + "\\ImgurSniper.UI.exe");
                } catch(Exception) {
                    // ignored
                }
            });
            menu.MenuItems.Add("Exit", delegate {
                System.Windows.Application.Current.Shutdown();
            });
            menu.MenuItems[1].PerformSelect();

            _nicon = new NotifyIcon {
                Icon = Properties.Resources.Logo,
                ContextMenu = menu,
                Visible = true,
                Text = $"Click or Press " + (usePrint ? "Print" : "Ctrl + Shift + " + sKey) + " to Snipe a new Image!"
            };

            _nicon.MouseClick += (sender, e) => {
                if(e.Button == MouseButtons.Left)
                    Crop(false);
            };

            System.Windows.Application.Current.Exit += delegate {
                _nicon.Icon = null;
                _nicon.Visible = false;
                _nicon.Dispose();
                _nicon = null;
            };


            HotKey hk = usePrint ? new HotKey(ModifierKeys.None, Key.Print, this) : new HotKey(ModifierKeys.Control | ModifierKeys.Shift, sKey, this);
            hk.HotKeyPressed += OpenFromShortcut;
        }

        //Open Snipe by Shortcut (Ctrl + Shift + I or Print)
        private void OpenFromShortcut(HotKey h) {
            h.HotKeyPressed -= OpenFromShortcut;
            Crop(false);
            h.HotKeyPressed += OpenFromShortcut;
        }

        private async void InstantUpload(string path) {
            await Task.Delay(550);

            string lpath = path.ToLower();
            if(lpath.EndsWith(".jpeg") ||
                lpath.EndsWith(".jpg") ||
                lpath.EndsWith(".png") ||
                lpath.EndsWith(".gif") ||
                lpath.EndsWith(".apng") ||
                lpath.EndsWith(".tiff") ||
                lpath.EndsWith(".xcf") ||
                lpath.EndsWith(".pdf")) {

                byte[] byteImg = File.ReadAllBytes(path);

                string KB = $"{(byteImg.Length / 1024d):0.#}";
                SuccessToast.Show($"Uploading Image... ({KB} KB)", TimeSpan.FromDays(10));

                await UploadImageToImgur(byteImg);
            } else {
                await ErrorToast.ShowAsync("Error, File is non supported Image Type!", TimeSpan.FromSeconds(5));
            }
            DelayedClose(0);
        }

        //Initialize important Variables
        private void Initialize() {
            _dir = FileIO._path;
            //Get configured Path
            string[] lines = FileIO.ReadConfig();
            foreach(string line in lines) {
                string[] config = line.Split(';');

                if(config[0] == "Path") {
                    _dir = config[1];
                    break;
                }
            }

            if(!Directory.Exists(_dir)) {
                Directory.CreateDirectory(_dir);
            }

            _imgur = new ImgurIO();
        }

        //Position Window correctly
        private void Position() {
            this.Top = SystemParameters.WorkArea.Height - this.Height;
            this.Width = SystemParameters.WorkArea.Width;
            this.Left = System.Windows.Forms.Screen.PrimaryScreen.Bounds.X;
        }

        //Make Screenshot, Let user Crop, Upload Picture and Copy Link to Clipboard
        private async void Crop(bool CloseOnFinish) {
            string[] lines = FileIO.ReadConfig();

            ScreenshotWindow window = new ScreenshotWindow(FileIO.AllMonitors);
            window.ShowDialog();
            this.Topmost = true;
            this.Visibility = Visibility.Visible;

            if(window.DialogResult == true) {

                byte[] cimg = window.CroppedImage;

                if(cimg.Length >= 10240000 && !FileIO.TokenExists) {
                    await ErrorToast.ShowAsync("Image Size exceeds 10MB, to increase this please Login to Imgur!", TimeSpan.FromSeconds(3));
                    return;
                }


                try {
                    bool Imgur = true;

                    foreach(string line in lines) {
                        string[] config = line.Split(';');

                        switch(config[0]) {
                            case "SaveImages":
                                //Config: Save Image locally?
                                if(bool.Parse(config[1])) {
                                    long time = DateTime.Now.ToFileTimeUtc();
                                    string extension = FileIO.UsePNG ? ".png" : ".jpeg";
                                    File.WriteAllBytes(_dir + $"\\Snipe_{time}{extension}", cimg);
                                }
                                break;
                            case "AfterSnipeAction":
                                //Config: Upload Image to Imgur or Copy to Clipboard?
                                if(config[1] != "Imgur") {
                                    Imgur = false;
                                }
                                break;
                        }
                    }

                    if(Imgur) {
                        string KB = $"{(cimg.Length / 1024d):0.#}";
                        SuccessToast.Show($"Uploading Image... ({KB} KB)", TimeSpan.FromDays(10));

                        await UploadImageToImgur(cimg);
                    } else {
                        CopyImageToClipboard(cimg);
                    }

                } catch(Exception ex) {
                    File.Delete(FileIO._config);

                    ErrorToast.Show($"An unknown Error occured! (Show this to the Smart Computer Apes: \"{ex}\")",
                        TimeSpan.FromSeconds(3.5));
                }
            }

            if(CloseOnFinish)
                DelayedClose(0);
            else
                this.Visibility = Visibility.Collapsed;
        }

        //Upload byte[] to imgur and give user a response
        private async Task UploadImageToImgur(byte[] cimg) {
            string link = await UploadImgur(cimg);

            if(link.StartsWith("http://")) {
                Clipboard.SetText(link);
                PlayBlop();

                if(FileIO.OpenAfterUpload)
                    System.Diagnostics.Process.Start(link);

                await SuccessToast.ShowAsync("Link to Imgur copied to Clipboard!",
                    TimeSpan.FromSeconds(5));
            } else {
                await ErrorToast.ShowAsync($"Error uploading Image to Imgur! ({link})",
                    TimeSpan.FromSeconds(5));
            }
        }

        //Copy the Byte[] to the Clipboard
        private void CopyImageToClipboard(byte[] cimg) {
            CopyClipboard(cimg);
            SuccessToast.Show("Image was copied to Clipboard!",
                TimeSpan.FromSeconds(3.5));
        }

        //Upload Image to Imgur and returns URL to Imgur
        private async Task<string> UploadImgur(byte[] cimg) {
            string response = await _imgur.Upload(cimg);

            //Copy Link to Clipboard
            Clipboard.SetText(response);

            return response;
        }

        //Parse byte[] to Image and write to Clipboard
        private void CopyClipboard(byte[] cimg) {
            //Parse byte[] to Images
            var image = new System.Windows.Media.Imaging.BitmapImage();
            using(var mem = new MemoryStream(cimg)) {
                mem.Position = 0;
                image.BeginInit();
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat;
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = null;
                image.StreamSource = mem;
                image.EndInit();
            }
            image.Freeze();

            //Copy whole Image to Clipboard
            Clipboard.SetImage(image);
        }

        //Close Window with short delay
        private async void DelayedClose(int Delay) {
            await Task.Delay(TimeSpan.FromMilliseconds(Delay));
            this.Close();
        }

        //Play the Blop Success Sound
        private void PlayBlop() {
            try {
                MediaPlayer player = new MediaPlayer();
                player.Volume = 30;

                string path = Path.Combine(FileIO._programFiles, "Resources\\Blop.wav");

                player.Open(new Uri(path));
                player.Play();
            } catch(Exception) { }
        }
    }
}
