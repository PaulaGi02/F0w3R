using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fl0w3R
{
    /// <summary>
    /// Fl0w3R v2: Simon-Says memory game controlled by the hand.
    /// Hand x selects one of four tiles, pushing the hand forward (depth relative to the body) grows the flower.
    /// </summary>
    public partial class MainWindow : Window
    {
        // === Kinect ===
        // Sensor and frame reader setup adapted from class examples (SM3603 Topic 03 + Topic 05)
        private KinectSensor sensor;

        private FrameDescription depthFrameDescription = null;
        private ushort[] depthData = null;
        private int bytesPerPixel = 4;        // depth -> color image
        private byte[] depthPixels = null;    // color channels
        private WriteableBitmap depthImageBitmap = null;

        private BodyIndexFrameReader bodyIndexFrameReader = null;
        private FrameDescription bodyIndexFrameDescription = null;
        private byte[] bodyIndexData = null;

        // === Flower frames (growth stages: 0 = bud, last = fully grown) ===
        private const int NUM_FLOWER_FRAMES = 5;
        private BitmapImage[] flowerFrames = new BitmapImage[NUM_FLOWER_FRAMES];

        // === Hand input ===
        // Filled by mouse simulation for now; later filled by Kinect hand segmentation.
        // Mouse simulation developed with help of Claude (Anthropic), Sept 2026.
        private bool useMouseSimulation = true;
        private bool handDetected = false;
        private double handX = 0.5;          // 0 = left edge, 1 = right edge
        private double handForwardMm = 0;    // distance of hand in front of body (mm)

        private const double HAND_OFFSET_MM = 250;     // hand counts as detected from here (Topic 5: dBody - offset)
        private const double SIM_MAX_FORWARD_MM = 600; // mouse at top of window = 600 mm

        // === Tiles ===
        private const int NUM_TILES = 4;
        private Border[] tiles;
        private Image[] tileImages;
        private int hoveredTile = -1; // -1 = no tile (hand in neutral layer)

        // === Layer logic (hand depth relative to body) ===
        private const double GROW_START_MM = 300; // growing starts here (small gap to pointing layer)
        private const double CONFIRM_MM = 450;    // flower fully grown = tile confirmed
        private int lockedTile = -1;              // tile is locked while its flower is growing
        private bool waitingForNeutral = false;   // after a confirm, hand must return to neutral first

        // === Game state ===
        private enum GameState { Start, Playback, PlayerTurn, GameOver }
        private GameState currentState = GameState.Start;

        private List<int> sequence = new List<int>(); // tile indices 0..3
        private int playbackIndex = 0;
        private bool playbackShowing = false;         // true = a tile is blooming, false = pause
        private int playerIndex = 0;
        private int score = 0;
        private Random rng = new Random();
        private DispatcherTimer playbackTimer;

        private const double PLAYBACK_START_DELAY = 1.0; // pause before a sequence starts (keeps last feedback visible)
        private const double PLAYBACK_SHOW_SECONDS = 1.0; // how long one tile blooms
        private const double PLAYBACK_GAP_SECONDS = 0.4;  // pause between steps (makes repeated tiles distinguishable)


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
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("Kinect not available, running in mouse simulation mode: " + ex.Message);
            }

            // everything below works with or without Kinect
            LoadFlowerFrames();
            flowerPreviewImage.Source = flowerFrames[0];

            // collect tiles in arrays so they can be handled in loops
            tiles = new Border[] { tile0, tile1, tile2, tile3 };
            tileImages = new Image[] { tileImg0, tileImg1, tileImg2, tileImg3 };
            ResetAllTilesToBud();

            playbackTimer = new DispatcherTimer();
            playbackTimer.Tick += PlaybackTimer_Tick;
        }

        private void LoadFlowerFrames()
        {
            for (int i = 0; i < NUM_FLOWER_FRAMES; i++)
            {
                string uriPath = $"pack://application:,,,/Assets/flower{i + 1}.png";
                flowerFrames[i] = new BitmapImage(new Uri(uriPath, UriKind.Absolute));
            }
        }

        // Kinect frames

        // Stores the latest player mask (255 = background, 0-5 = player); code from class (Topic 05)
        private void BodyIndexFrameReader_FrameArrived(object sender, BodyIndexFrameArrivedEventArgs e)
        {
            using (BodyIndexFrame bodyIndexFrame = e.FrameReference.AcquireFrame())
            {
                if (bodyIndexFrame == null) return;
                bodyIndexFrame.CopyFrameDataToArray(bodyIndexData);
            }
        }

        // Copies depth data and visualizes it (player in green); code adapted from class (Topic 03 + 05).
        // Hand segmentation will be added here in step 7.
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame == null) return;

                depthFrame.CopyFrameDataToArray(depthData);

                ushort minDepth = depthFrame.DepthMinReliableDistance;
                ushort maxDepth = depthFrame.DepthMaxReliableDistance;

                for (int x = 0; x < depthFrameDescription.Width; x++)
                    for (int y = 0; y < depthFrameDescription.Height; y++)
                    {
                        int index = y * depthFrameDescription.Width + x;
                        ushort depth = depthData[index];

                        bool isPerson = bodyIndexData != null && bodyIndexData[index] != 0xff;

                        if (isPerson)
                        {
                            depthPixels[bytesPerPixel * index] = 0;
                            depthPixels[bytesPerPixel * index + 1] = 255; // green
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

        // Hand input

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

            OnHandInputUpdated();
        }

        // Central place for hand input: called by the mouse simulation now,
        // later by the Kinect hand tracking. Layer logic developed with help of Claude (Anthropic), Sept 2026.
        private void OnHandInputUpdated()
        {
            if (tiles == null) return; // window not fully loaded yet

            // 0. input only counts during the player's turn
            if (currentState != GameState.PlayerTurn)
            {
                if (hoveredTile != -1)
                {
                    hoveredTile = -1;
                    UpdateTileHighlight();
                }
                debugText.Text = $"Input inactive ({currentState})";
                return;
            }

            // 1. after a confirmation: ignore input until the hand is back in the neutral layer
            if (waitingForNeutral)
            {
                if (!handDetected)
                {
                    waitingForNeutral = false;
                    ResetAllTilesToBud();
                    hoveredTile = -1;
                    UpdateTileHighlight();
                }
                debugText.Text = "Layer: waiting for neutral";
                return;
            }

            // 2. how far the flower has grown: 0 = bud, 1 = fully grown (Map from Topic 5)
            double growth = Clamp01(Map(handForwardMm, GROW_START_MM, CONFIRM_MM, 0, 1));

            // 3. which tile is under the hand (x position), -1 = no hand
            int tileUnderHand = handDetected ? Math.Min((int)(handX * NUM_TILES), NUM_TILES - 1) : -1;

            // 4. growing starts -> lock the tile, so sideways drift while pushing doesn't switch tiles
            if (lockedTile == -1 && tileUnderHand != -1 && growth > 0)
                lockedTile = tileUnderHand;

            // 5. hand pulled back before the flower was fully grown -> shrink back to bud and unlock
            if (lockedTile != -1 && growth <= 0)
            {
                SetTileGrowth(lockedTile, 0);
                lockedTile = -1;
            }

            hoveredTile = (lockedTile != -1) ? lockedTile : tileUnderHand;

            // 6. let the locked flower grow; fully grown = confirmed
            if (lockedTile != -1)
            {
                SetTileGrowth(lockedTile, growth);
                if (growth >= 1.0)
                {
                    int confirmedTile = lockedTile;
                    lockedTile = -1;
                    waitingForNeutral = true;
                    OnTileConfirmed(confirmedTile);
                }
            }

            UpdateTileHighlight();

            string layer = !handDetected ? "neutral" : (growth > 0 ? "grow" : "point");
            debugText.Text = $"Layer: {layer} | Tile: {hoveredTile} | Growth: {growth:P0}";
        }

        //  Tiles 

        // Hovered tile gets a white frame, the others are dimmed; no hand -> all normal
        private void UpdateTileHighlight()
        {
            for (int i = 0; i < NUM_TILES; i++)
            {
                bool isHovered = (i == hoveredTile);
                tiles[i].BorderBrush = isHovered ? Brushes.White : Brushes.Transparent;
                tiles[i].Opacity = (hoveredTile == -1 || isHovered) ? 1.0 : 0.5;
            }
        }

        // shows the flower frame that matches the growth (0..1) on one tile
        private void SetTileGrowth(int tile, double growth)
        {
            int frame = (int)Math.Round(growth * (flowerFrames.Length - 1));
            tileImages[tile].Source = flowerFrames[frame];
        }

        private void ResetAllTilesToBud()
        {
            for (int i = 0; i < NUM_TILES; i++)
                SetTileGrowth(i, 0);
        }

        // linear mapping (from class, Topic 05)
        private double Map(double value, double start1, double stop1, double start2, double stop2)
        {
            return (value - start1) / (stop1 - start1) * (stop2 - start2) + start2;
        }

        private double Clamp01(double v)
        {
            return Math.Max(0, Math.Min(1, v));
        }

        //Game flow
        // Start -> Playback -> PlayerTurn -> (all correct) Playback with +1 step / (wrong) GameOver

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            score = 0;
            sequence.Clear();
            for (int i = 0; i < 2; i++) // start with 2 steps
                sequence.Add(rng.Next(NUM_TILES));

            ResetAllTilesToBud();
            hoveredTile = -1;
            UpdateTileHighlight();

            startScreenGrid.Visibility = Visibility.Collapsed;
            gameOverGrid.Visibility = Visibility.Collapsed;
            gameScreenGrid.Visibility = Visibility.Visible;

            StartPlayback();
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            gameOverGrid.Visibility = Visibility.Collapsed;
            startScreenGrid.Visibility = Visibility.Visible;
            currentState = GameState.Start;
        }

        // Plays the sequence on the tiles: bloom -> pause -> bloom -> ...
        private void StartPlayback()
        {
            currentState = GameState.Playback;
            playbackIndex = 0;
            // DEBUG: print the full sequence of this round (remove later)
            System.Console.WriteLine($"Round start, sequence: {string.Join(", ", sequence)}");
            playbackShowing = false;
            lockedTile = -1;
            waitingForNeutral = false;

            statusText.Text = $"Remember... Score: {score}";
            statusText.Foreground = Brushes.Orange;

            // short delay first, so the last confirmed flower stays visible as feedback
            playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_START_DELAY);
            playbackTimer.Start();
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (!playbackShowing)
            {
                // pause is over -> show next step, or hand over to the player
                if (playbackIndex >= sequence.Count)
                {
                    playbackTimer.Stop();
                    StartPlayerTurn();
                    return;
                }

                ResetAllTilesToBud();
                SetTileGrowth(sequence[playbackIndex], 1.0);
                // DEBUG: print which step is shown (remove later)
                System.Console.WriteLine($"  showing step {playbackIndex}: tile {sequence[playbackIndex]}");
                playbackShowing = true;
                playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_SHOW_SECONDS);
            }
            else
            {
                // bloom is over -> short pause, so the same tile twice in a row is visible as two steps
                ResetAllTilesToBud();
                playbackShowing = false;
                playbackIndex++;
                playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_GAP_SECONDS);
            }
        }

        private void StartPlayerTurn()
        {
            currentState = GameState.PlayerTurn;
            playerIndex = 0;
            lockedTile = -1;
            waitingForNeutral = true; // hand must start from neutral (prevents accidental instant input)
            ResetAllTilesToBud();

            statusText.Text = $"Your turn! 0/{sequence.Count}";
            statusText.Foreground = Brushes.Lime;
        }

        // called once when a flower is fully grown
        private void OnTileConfirmed(int tile)
        {
            if (currentState != GameState.PlayerTurn) return;

            if (tile == sequence[playerIndex])
            {
                playerIndex++;
                statusText.Text = $"Your turn! {playerIndex}/{sequence.Count}";

                if (playerIndex >= sequence.Count)
                {
                    // round complete -> next round with one more step
                    score = sequence.Count;
                    sequence.Add(rng.Next(NUM_TILES));
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
            playbackTimer.Stop();
            currentState = GameState.GameOver;
            gameOverText.Text = "Game Over";
            finalScoreText.Text = $"Score: {score}";
            gameScreenGrid.Visibility = Visibility.Collapsed;
            gameOverGrid.Visibility = Visibility.Visible;
        }
    }
}
