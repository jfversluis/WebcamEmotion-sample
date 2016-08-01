using Microsoft.ProjectOxford.Emotion;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Devices.Sensors;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace WebcamEmotion
{
    // Borrowed some code from the CameraStarterKit sample https://github.com/Microsoft/Windows-universal-samples/tree/master/Samples/CameraStarterKit/
    public sealed partial class MainPage : Page
    {
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
        private readonly SimpleOrientationSensor _orientationSensor = SimpleOrientationSensor.GetDefault();
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        private StorageFolder _captureFolder = null;
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();
        private MediaCapture _mediaCapture;
        private bool _isInitialized;
        private bool _isPreviewing;
        private bool _mirroringPreview;
        private bool _externalCamera;
        private DispatcherTimer _timer = new DispatcherTimer();

        #region Constructor, lifecycle and navigation

        public MainPage()
        {
            InitializeComponent();

            NavigationCacheMode = NavigationCacheMode.Disabled;

            Application.Current.Suspending += Application_Suspending;
            Application.Current.Resuming += Application_Resuming;

            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += _timer_Tick;
        }

        private async void Application_Suspending(object sender, SuspendingEventArgs e)
        {
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                var deferral = e.SuspendingOperation.GetDeferral();

                await CleanupCameraAsync();

                deferral.Complete();
            }
        }

        private async void Application_Resuming(object sender, object o)
        {
            if (Frame.CurrentSourcePageType == typeof(MainPage))
            {
                await SetupUiAsync();

                await InitializeCameraAsync();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await SetupUiAsync();

            await InitializeCameraAsync();
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            await CleanupCameraAsync();
        }

        #endregion Constructor, lifecycle and navigation

        #region Event handlers

        private async void _timer_Tick(object sender, object e)
        {
            if (_isInitialized)
                await TakePhotoAsync();
        }

        private async void SystemMediaControls_PropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (args.Property == SystemMediaTransportControlsProperty.SoundLevel && Frame.CurrentSourcePageType == typeof(MainPage))
                {
                    if (sender.SoundLevel == SoundLevel.Muted)
                        await CleanupCameraAsync();
                    else if (!_isInitialized)
                        await InitializeCameraAsync();
                }
            });
        }

        #endregion Event handlers

        #region MediaCapture methods

        private async Task InitializeCameraAsync()
        {
            Debug.WriteLine("InitializeCameraAsync");

            if (_mediaCapture == null)
            {
                var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Back);

                if (cameraDevice == null)
                {
                    Debug.WriteLine("No camera device found!");
                    return;
                }

                _mediaCapture = new MediaCapture();

                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                try
                {
                    await _mediaCapture.InitializeAsync(settings);
                    _isInitialized = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }

                if (_isInitialized)
                {
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        _externalCamera = true;
                    }
                    else
                    {
                        _externalCamera = false;

                        _mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    await StartPreviewAsync();
                }
            }
        }

        private async Task StartPreviewAsync()
        {
            _displayRequest.RequestActive();

            PreviewControl.Source = _mediaCapture;
            PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            await _mediaCapture.StartPreviewAsync();
            _isPreviewing = true;

            if (_isPreviewing)
                await SetPreviewRotationAsync();

            _timer.Start();
        }

        private async Task SetPreviewRotationAsync()
        {
            if (_externalCamera)
                return;

            var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);

            await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        private async Task StopPreviewAsync()
        {
            _timer.Stop();

            _isPreviewing = false;
            await _mediaCapture.StopPreviewAsync();

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                PreviewControl.Source = null;

                _displayRequest.RequestRelease();
            });
        }

        private async Task TakePhotoAsync()
        {
            var stream = new InMemoryRandomAccessStream();

            Debug.WriteLine("Taking photo...");
            await _mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);

            try
            {
                var file = await _captureFolder.CreateFileAsync("SimplePhoto.jpg", CreationCollisionOption.ReplaceExisting);

                Debug.WriteLine("Photo taken! Saving to " + file.Path);

                await ReencodeAndSavePhotoAsync(stream, file, PhotoOrientation.Normal);

                Debug.WriteLine("Photo saved!");

                var dispatcher = CoreWindow.GetForCurrentThread().Dispatcher;
                await Task.Run(async () =>
                {
                    var emotionClient = new EmotionServiceClient("c5aae10de9664134b010b3ae9bfbe405");
                    var emotionResult = await emotionClient.RecognizeAsync(File.Open(file.Path, FileMode.Open));

                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                    {
                        var image = new BitmapImage();
                        var f = await KnownFolders.PicturesLibrary.GetFileAsync(file.Name);
                        image.SetSource(await f.OpenAsync(FileAccessMode.Read));

                        EmotionImage.Source = image;
                        Emotion.Text = emotionResult[0].Scores.ToRankedList().First().Key;

                        switch (Emotion.Text)
                        {
                            case "Happiness":
                                Emotion.Text += " \ud83d\ude00";
                                break;

                            case "Anger":
                                Emotion.Text += " \ud83d\ude21";
                                break;

                            case "Contempt":
                                Emotion.Text += " \uD83D\uDE12";
                                break;

                            case "Disgust":
                                Emotion.Text += " \uD83D\uDE1D";
                                break;

                            case "Fear":
                                Emotion.Text += " \uD83D\uDE28";
                                break;

                            case "Sadness":
                                Emotion.Text += " \ud83d\ude22";
                                break;

                            case "Surprise":
                                Emotion.Text += " \ud83d\ude31";
                                break;

                            default:
                            case "Neutral":
                                Emotion.Text += " \uD83D\uDCA9";
                                break;
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception when taking a photo: " + ex.ToString());
            }
        }

        private async Task CleanupCameraAsync()
        {
            Debug.WriteLine("CleanupCameraAsync");

            if (_isInitialized)
            {
                if (_isPreviewing)
                    await StopPreviewAsync();

                _isInitialized = false;
            }

            if (_mediaCapture != null)
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }
        }

        #endregion MediaCapture methods

        #region Helper functions

        private async Task SetupUiAsync()
        {
            var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
            _captureFolder = picturesLibrary.SaveFolder ?? ApplicationData.Current.LocalFolder;
        }

        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
        {
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        private static async Task ReencodeAndSavePhotoAsync(IRandomAccessStream stream, StorageFile file, PhotoOrientation photoOrientation)
        {
            using (var inputStream = stream)
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);

                    var properties = new BitmapPropertySet { { "System.Photo.Orientation", new BitmapTypedValue(photoOrientation, PropertyType.UInt16) } };

                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();
                }
            }
        }

        #endregion Helper functions
    }
}