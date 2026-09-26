using Microsoft.Kinect;
using NUI3D;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;  

namespace Fl0w3R
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private KinectSensor sensor;

        private FrameDescription depthFrameDescription = null;
        private ushort[] depthData = null;
        private int bytesPerPixel = 4; // depth -> color image
        private byte[] depthPixels = null; // color channels 
        private WriteableBitmap depthImageBitmap = null;

        private ColorFrameManager colorFrameManager;

        //Base structure adapted from the class excises BodyIndexFrame integration and depth Zone detection
        private BodyIndexFrameReader bodyIndexFrameReader = null;
        private FrameDescription bodyIndexFrameDescription = null;
        private byte[] bodyIndexData = null;

        // Hand input
        // Filled by mouse simulation for now; later filled by Kinect hand segmentation.
        // Mouse simulation developed with help of Claude (Anthropic), Sept 2026.
        private bool useMouseSimulation = true;
        private bool handDetected = false;
        private double handX = 0.5;          // 0 = left edge, 1 = right edge
        private double handForwardMm = 0;    // distance of hand in front of body (mm)

        private const double HAND_OFFSET_MM = 250;   // hand counts as detected from here (Topic 5: dBody - offset)
        private const double SIM_MAX_FORWARD_MM = 600; // mouse at top of window = 600 mm


        // Zone detection state
        private const int num_zo = 5;
        private int curZone = -1; // no valid Zone (no person detected)

        // Adding defined Zones in which the Frames change (leaving some space inbetween because of noise)
        private readonly int[,] zoneRanges = new int[num_zo, 2]
        {   // Zone 1-5
            {500, 620},
            {630, 700},
            {710, 780},
            {790, 860},
            {870, 940},
        };

        private Queue<int> recentDepths = new Queue<int>();
        private const int SMOOTHING_WINDOW = 8;

        // flower frame images loading
        private BitmapImage[] flowerFrames = new BitmapImage[num_zo];

        // dwell state 
        private int dwZo = -1; // which zone we are currretnly timing
        private DateTime dwellStartTime;
        private const double DWELL_SECONDS = 2.0;

        private enum GameState { Start, Playback, PlayerTurn, GameOver }
        private GameState currentState = GameState.Start;

        private List<int> sequence = new List<int>();
        private int playbackIndex = 0;
        private int playerIndex = 0;
        private int score = 0;
        private Random rng = new Random();
        private DispatcherTimer playbackTimer;


        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine("window loaded");

            // Kinect setup is wrapped in try/catch so the app also runs without a sensor/SDK
            // (mouse simulation mode). Guard added with help of Claude (Anthropic), Sept 2026.
            try
            {
                sensor = KinectSensor.GetDefault();

                DepthFrameReader depthFrameReader = sensor.DepthFrameSource.OpenReader();
                depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;

                bodyIndexFrameReader = sensor.BodyIndexFrameSource.OpenReader();
                bodyIndexFrameReader.FrameArrived += BodyIndexFrameReader_FrameArrived;
                bodyIndexFrameDescription = sensor.BodyIndexFrameSource.FrameDescription;
                bodyIndexData = new byte[bodyIndexFrameDescription.LengthInPixels];

                sensor.Open();

                depthFrameDescription = sensor.DepthFrameSource.FrameDescription;
                depthData = new ushort[depthFrameDescription.LengthInPixels];
                depthPixels = new byte[depthFrameDescription.LengthInPixels * bytesPerPixel];

                depthImageBitmap = new WriteableBitmap(
                    depthFrameDescription.Width, depthFrameDescription.Height,
                    96, 96, PixelFormats.Bgr32, null);
                kinectDepthImage.Source = depthImageBitmap;
                kinectDepthImageStart.Source = depthImageBitmap;

                colorFrameManager = new ColorFrameManager();
                colorFrameManager.Init(sensor, kinectColorImage);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("Kinect not available, running in mouse simulation mode: " + ex.Message);
            }

            // everything below works with or without Kinect
            LoadFlowerFrames();
            flowerPreviewImage.Source = flowerFrames[0]; // now AFTER loading (fixes bug B2.1)

            playbackTimer = new DispatcherTimer();
            playbackTimer.Interval = TimeSpan.FromSeconds(1.5);
            playbackTimer.Tick += PlaybackTimer_Tick;
        }


        private void LoadFlowerFrames()
        {
            for (int i = 0; i < num_zo; i++)
            {
                string uriPath = $"pack://application:,,,/Assets/flower{i + 1}.png";
                flowerFrames[i] = new BitmapImage(new Uri(uriPath, UriKind.Absolute));
            }
        }

        // Store latst body index frame because ...
        private void BodyIndexFrameReader_FrameArrived(object sender, BodyIndexFrameArrivedEventArgs e)
        {
            using (BodyIndexFrame bodyIndexFrame = e.FrameReference.AcquireFrame())
            {
                if (bodyIndexFrame == null) return;
                bodyIndexFrame.CopyFrameDataToArray(bodyIndexData);
            }
        }

        // old code
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            // using statement automatically takes care of disposing of 
            // the DepthFrame object when you are done using it
            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame == null) return;

                depthFrame.CopyFrameDataToArray(depthData);

                // depth data to pixels
                ushort minDepth = depthFrame.DepthMinReliableDistance;
                ushort maxDepth = depthFrame.DepthMaxReliableDistance;

                // find nearest depth value
                int nearestDepth = FindNearestDepthInBodyMask();
                UpdateZoneFromDepth(nearestDepth);
                UpdateDwellProcess();
                UpdateDebugText(nearestDepth);

                // visualize depth image (code from class)
                for (int x = 0; x < depthFrameDescription.Width; x++)
                    for (int y = 0; y < depthFrameDescription.Height; y++)
                    {
                        int index = y * depthFrameDescription.Width + x;
                        ushort depth = depthData[index];


                        // highlight person for better seeing/ undertanding
                        bool isPerson = bodyIndexData != null && bodyIndexData[index] != 0xff;

                        if (isPerson)
                        {
                            depthPixels[bytesPerPixel * index] = 0;
                            depthPixels[bytesPerPixel * index + 1] = 255; //green
                            depthPixels[bytesPerPixel * index + 2] = 0;
                        }
                        else
                        {
                            byte depthByte = (byte)((depth - minDepth) * 255.0 / (maxDepth - minDepth));
                            depthByte = (byte)(255 - depthByte);
                            depthPixels[bytesPerPixel * index] = depthByte;
                            depthPixels[bytesPerPixel * index + 1] = depthByte;
                            depthPixels[bytesPerPixel * index + 2] = depthByte;
                        }
                    }



                depthImageBitmap.WritePixels(
        new Int32Rect(0, 0, depthFrameDescription.Width, depthFrameDescription.Height),
        depthPixels,
        depthFrameDescription.Width * bytesPerPixel, 0);
            }
        }

        // scan only pixels that belong to a person, return the smallest depth found 
        private int FindNearestDepthInBodyMask()
        {
            if (bodyIndexData == null || depthData == null) return -1;

            int nearest = int.MaxValue;
            bool found = false;

            for (int i = 0; i < bodyIndexData.Length; i++)
            {
                // 0xff means "no body detected at this pixel"; any other value (0-5) is a body ID
                if (bodyIndexData[i] != 0xff)
                {
                    ushort depth = depthData[i];
                    if (depth > 0 && depth < nearest) // depth == 0 means invalid reading
                    {
                        nearest = depth;
                        found = true;
                    }
                }
            }

            return found ? nearest : -1;
        }

        // smoothing + zone classification with hysteresis 
        private void UpdateZoneFromDepth(int rawDepth)
        {
            if (rawDepth <= 0)
            {
                // no person detected — don't change curZone here,
                return;
            }

            // moving average to reduce sensor noise
            recentDepths.Enqueue(rawDepth);
            if (recentDepths.Count > SMOOTHING_WINDOW)
                recentDepths.Dequeue();

            double avgDepth = recentDepths.Average();

            // check which fixed zone the smoothed depth falls into
            for (int z = 0; z < num_zo; z++)
            {
                if (avgDepth >= zoneRanges[z, 0] && avgDepth <= zoneRanges[z, 1])
                {
                    if (curZone != z + 1)
                    {
                        curZone = z + 1;
                        UpdateFlowerImage();
                    }
                    return;
                }
            }

            // if we reach here, avgDepth is inside a buffer gap between zones —
            // hysteresis: keep the last valid zone unchanged instead of flickering
        }
        private void UpdateFlowerImage()
        {
            if (flowerImage == null || curZone < 1 || curZone > num_zo) return;
            flowerImage.Source = flowerFrames[curZone - 1];

        }
        private void UpdateDebugText(int rawDepth)
        {
            string zoneLabel = curZone > 0 ? curZone.ToString() : "--";
            string depthLabel = rawDepth > 0 ? rawDepth.ToString() : "--";
            debugText.Text = $"Zone: {zoneLabel} | Depth: {depthLabel} mm";
        }

        private void UpdateDwellProcess()
        {
            if (curZone < 1)
            {
                dwZo = -1;
                if (dwellProgressBar != null) dwellProgressBar.Value = 0;
                return;
            }
            if (curZone != dwZo)
            {
                dwZo = curZone;
                dwellStartTime = DateTime.Now;
            }

            double elapsedSeconds = (DateTime.Now - dwellStartTime).TotalSeconds;
            double progress = Math.Min(elapsedSeconds / DWELL_SECONDS, 1.0);

            if (dwellProgressBar != null)
                dwellProgressBar.Value = progress * 100;

            if (progress >= 1.0)
            {
                OnZoneConfirmed(dwZo);
                dwZo = -1; // reset so it needs to leave and re-enter to confirm again
                if (dwellProgressBar != null) dwellProgressBar.Value = 0;
            }
        }

        // NEW: starts a fresh game when the Start button is pressed
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            score = 0;
            sequence.Clear();
            for (int i = 0; i < 2; i++) // start with 2 frames, as decided
                sequence.Add(rng.Next(1, num_zo + 1));

            startScreenGrid.Visibility = Visibility.Collapsed;
            gameOverGrid.Visibility = Visibility.Collapsed;
            gameScreenGrid.Visibility = Visibility.Visible;

            StartPlayback();
        }

        // NEW: returns to the start screen after Game Over
        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            gameOverGrid.Visibility = Visibility.Collapsed;
            startScreenGrid.Visibility = Visibility.Visible;
            currentState = GameState.Start;
        }

        private void StartPlayback()
        {
            currentState = GameState.Playback;
            playbackIndex = 0;
            statusText.Text = $"Remember... Score: {score}";
            statusText.Foreground = System.Windows.Media.Brushes.Orange;
            ShowSequenceFrame();
            playbackTimer.Start();
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            playbackIndex++;
            if (playbackIndex >= sequence.Count)
            {
                playbackTimer.Stop();
                StartPlayerTurn();
                return;
            }
            ShowSequenceFrame();
        }

        private void ShowSequenceFrame()
        {
            int frame = sequence[playbackIndex];
            flowerImage.Source = flowerFrames[frame - 1];
        }

        private void StartPlayerTurn()
        {
            currentState = GameState.PlayerTurn;
            playerIndex = 0;
            statusText.Text = $"Your Turn! Score: {score}";
            statusText.Foreground = System.Windows.Media.Brushes.Lime;
            flowerImage.Source = null; // clear during player's turn, or leave last frame — your choice
        }


        private void OnZoneConfirmed(int zone)
        {
            if (currentState != GameState.PlayerTurn) return; // only react during player's turn

            if (zone == sequence[playerIndex])
            {
                playerIndex++;
                flowerImage.Source = flowerFrames[zone - 1]; // brief visual feedback

                if (playerIndex >= sequence.Count)
                {
                    // round complete — success
                    score = sequence.Count;
                    sequence.Add(rng.Next(1, num_zo + 1)); // +1 new frame
                    StartPlayback();
                }
            }
            else
            {
                TriggerGameOver();
            }
        }

        private void TriggerGameOver()
        {
            currentState = GameState.GameOver;
            gameOverText.Text = "Game Over";
            finalScoreText.Text = $"Score: {score}";
            gameScreenGrid.Visibility = Visibility.Collapsed;
            gameOverGrid.Visibility = Visibility.Visible;
        }

        // Simulates the hand with the mouse: X = left/right, Y = how far the hand is pushed forward
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (!useMouseSimulation) return;

            Point p = e.GetPosition(this);
            handX = Math.Max(0, Math.Min(1, p.X / ActualWidth));
            handForwardMm = (1 - p.Y / ActualHeight) * SIM_MAX_FORWARD_MM; // bottom = 0, top = max
            handDetected = handForwardMm >= HAND_OFFSET_MM;

            // temporary debug output in the window title (always visible)
            Title = $"Fl0w3R | Hand: {(handDetected ? "yes" : "no")} | X: {handX:0.00} | forward: {handForwardMm:0} mm";
        }


        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            Array.Clear(depthPixels, 0, depthPixels.Length);
        }
    }
}

