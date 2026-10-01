
using Microsoft.Kinect;
using Emgu.CV;
using Emgu.CV.Structure;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
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
    /// Fl0w3R is a memory game controlled with the hand.
    /// The x position of the hand picks one of four tiles,
    /// pushing the hand forward lets the flower grow.
    public partial class MainWindow : Window
    {
        //  Kinect 
        // Sensor and frame reader setup adapted from class examples (SM3603 Topic 03 + Topic 05)
        private KinectSensor sensor;

        private FrameDescription depthFrameDescription = null; // size of the depth frame (512 x 424)
        private ushort[] depthData = null;       // raw depth per pixel in mm
        private int bytesPerPixel = 4;           // BGR32 image: blue, green, red, unused
        private byte[] depthPixels = null;       // the colored depth image I show on screen
        private WriteableBitmap depthImageBitmap = null; // WPF bitmap that displays depthPixels

        private BodyIndexFrameReader bodyIndexFrameReader = null;
        private FrameDescription bodyIndexFrameDescription = null;
        private byte[] bodyIndexData = null;     // per pixel: 255 = background, 0-5 = a detected person

        //  Flower frames
        // 5 images of the growing flower: frame 0 = bud, frame 4 = fully grown
        private const int NUM_FLOWER_FRAMES = 5;
        private BitmapImage[] flowerFrames = new BitmapImage[NUM_FLOWER_FRAMES];

        // Hand input 
        // These three values are everything the game needs from the hand.
        // They come either from the mouse (for testing without Kinect) or from the Kinect; switch with key M.
        private bool useMouseSimulation = true;  // true = mouse, false = Kinect
        private bool handDetected = false;       // is there a hand in front of the body?
        private double handX = 0.5;              // hand position from 0 (left) to 1 (right)
        private double handForwardMm = 0;        // how far the hand is in front of the body (mm)

        private const double HAND_OFFSET_MM = 250;     // from here on the hand counts as detected (Topic 5: dBody - offset)
        private const double SIM_MAX_FORWARD_MM = 600; // mouse at the top of the window = hand 600 mm in front

        // Kinect hand segmentation
        // Idea from Topic 05: everything of the player that is clearly in front of the body is the hand.
        // Contour detection with Emgu CV from Topic 04/05.
        private Image<Gray, byte> handMaskImg;       // black and white image: white = hand pixels
        private const int DEPTH_WIDTH = 512;          // Kinect v2 depth resolution
        private const int DEPTH_HEIGHT = 424;
        private const int MIN_BODY_PIXELS = 3000;     // if fewer player pixels are found, nobody is really there
        private const double MIN_HAND_AREA = 500;     // blobs smaller than this are noise, not a hand
        private bool mirrorX = false;                 // set to true if left and right are swapped

        // Tiles 
        private const int NUM_TILES = 4;
        private Border[] tiles;          // the four colored tiles (so I can loop over them)
        private Image[] tileImages;      // the flower image inside each tile
        private int hoveredTile = -1;    // tile the hand currently points at, -1 = none

        // Layer logic
        // The depth of the hand relative to the body decides what happens:
        // neutral (< 250 mm) -> point (250-300 mm) -> grow (300-450 mm) -> confirmed (450 mm)
        private const double GROW_START_MM = 300;  // the flower starts growing here (small gap after pointing)
        private const double CONFIRM_MM = 450;     // flower fully grown = tile is chosen
        private int lockedTile = -1;               // while a flower grows, its tile stays locked
        private bool waitingForNeutral = false;    // after choosing, the hand has to go back to the body first

        // Game state 
        private enum GameState { Start, Playback, PlayerTurn, GameOver }
        private GameState currentState = GameState.Start;

        private List<int> sequence = new List<int>(); // the order the player has to remember (tile numbers 0-3)
        private int playbackIndex = 0;                // which step of the sequence is shown right now
        private bool playbackShowing = false;         // true = a flower is blooming, false = short pause
        private int playerIndex = 0;                  // which step the player has to enter next
        private int score = 0;
        private Random rng = new Random();
        private DispatcherTimer playbackTimer;        // timer that plays the sequence step by step

        private const double PLAYBACK_START_DELAY = 1.0; // pause before the sequence starts
        private const double PLAYBACK_SHOW_SECONDS = 1.0; // how long one flower blooms
        private const double PLAYBACK_GAP_SECONDS = 0.4;  // pause between steps, so the same tile twice is visible as two steps


        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine("window loaded");

            // Emgu CV: create the hand mask image once.
            // Own try/catch, so an Emgu problem is not mistaken for a Kinect problem.
            try
            {
                handMaskImg = new Image<Gray, byte>(DEPTH_WIDTH, DEPTH_HEIGHT);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("Emgu CV not available: " + ex.Message);
            }

            // Kinect setup in try/catch, so the app also runs without a sensor (then mouse only).
            try
            {
                sensor = KinectSensor.GetDefault();

                // depth reader: calls DepthFrameReader_FrameArrived for every new depth frame
                DepthFrameReader depthFrameReader = sensor.DepthFrameSource.OpenReader();
                depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;

                // body index reader: tells me which pixels belong to a person
                bodyIndexFrameReader = sensor.BodyIndexFrameSource.OpenReader();
                bodyIndexFrameReader.FrameArrived += BodyIndexFrameReader_FrameArrived;
                bodyIndexFrameDescription = sensor.BodyIndexFrameSource.FrameDescription;
                bodyIndexData = new byte[bodyIndexFrameDescription.LengthInPixels];

                sensor.Open();

                // arrays for the depth data and the colored depth image
                depthFrameDescription = sensor.DepthFrameSource.FrameDescription;
                depthData = new ushort[depthFrameDescription.LengthInPixels];
                depthPixels = new byte[depthFrameDescription.LengthInPixels * bytesPerPixel];

                // one bitmap, shown on the start screen and on the game screen
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
            flowerPreviewImage.Source = flowerFrames[0]; // flower on the start screen (after loading!)

            // put the tiles into arrays so I can handle them in loops
            tiles = new Border[] { tile0, tile1, tile2, tile3 };
            tileImages = new Image[] { tileImg0, tileImg1, tileImg2, tileImg3 };
            ResetAllTilesToBud();

            playbackTimer = new DispatcherTimer();
            playbackTimer.Tick += PlaybackTimer_Tick;
        }

        // loads flower1.png ... flower5.png from the Assets folder
        private void LoadFlowerFrames()
        {
            for (int i = 0; i < NUM_FLOWER_FRAMES; i++)
            {
                string uriPath = $"pack://application:,,,/Assets/flower{i + 1}.png";
                flowerFrames[i] = new BitmapImage(new Uri(uriPath, UriKind.Absolute));
            }
        }

        //Kinect frames

        // saves the newest body index frame, so the depth handler knows which pixels are a person
        // (code from class, Topic 05)
        private void BodyIndexFrameReader_FrameArrived(object sender, BodyIndexFrameArrivedEventArgs e)
        {
            using (BodyIndexFrame bodyIndexFrame = e.FrameReference.AcquireFrame())
            {
                if (bodyIndexFrame == null) return;
                bodyIndexFrame.CopyFrameDataToArray(bodyIndexData);
            }
        }

        // runs for every depth frame (about 30 times per second):
        // 1. estimate the body depth, 2. find the hand pixels and color the image, 3. get the hand values
        // Colors: background = gray depth, body = green, hand = yellow.
        // Visualization adapted from class (Topic 03 + 05), hand segmentation idea from Topic 05.
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame == null) return;

                depthFrame.CopyFrameDataToArray(depthData);

                ushort minDepth = depthFrame.DepthMinReliableDistance; // 500 mm
                ushort maxDepth = depthFrame.DepthMaxReliableDistance; // 4500 mm

                // 1. body depth = average depth of all player pixels
                //    (the hand is only a small part of the body, so it barely changes the average)
                float dBody = CalculateBodyDepth(out int bodyPixelCount);
                bool bodyFound = bodyPixelCount > MIN_BODY_PIXELS;
                float handThreshold = dBody - (float)HAND_OFFSET_MM; // everything closer than this is the hand

                // 2. build the hand mask and the colored depth image in one loop
                byte[,,] mask = handMaskImg != null ? handMaskImg.Data : null;
                int width = depthFrameDescription.Width;

                for (int y = 0; y < depthFrameDescription.Height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;   // 2D position -> position in the 1D array
                        ushort depth = depthData[index];

                        bool isPerson = bodyIndexData[index] != 0xff;
                        bool isHand = bodyFound && isPerson && depth > 0 && depth < handThreshold;
                        if (mask != null) mask[y, x, 0] = isHand ? (byte)255 : (byte)0; // white = hand

                        int p = bytesPerPixel * index;
                        if (isHand)
                        {
                            depthPixels[p] = 0; depthPixels[p + 1] = 255; depthPixels[p + 2] = 255; // yellow
                        }
                        else if (isPerson)
                        {
                            depthPixels[p] = 0; depthPixels[p + 1] = 255; depthPixels[p + 2] = 0;   // green
                        }
                        else
                        {
                            // background as gray depth image: near = bright, far = dark
                            byte depthByte = (byte)((depth - minDepth) * 255.0 / (maxDepth - minDepth));
                            depthByte = (byte)(255 - depthByte);
                            depthPixels[p] = depthByte; depthPixels[p + 1] = depthByte; depthPixels[p + 2] = depthByte;
                        }
                    }

                // show the colored image on screen
                depthImageBitmap.WritePixels(
                    new Int32Rect(0, 0, width, depthFrameDescription.Height),
                    depthPixels, width * bytesPerPixel, 0);

                // 3. get the hand values from the mask (only when the Kinect controls the game)
                if (!useMouseSimulation && handMaskImg != null)
                    UpdateHandFromMask(dBody, bodyFound);
            }
        }

        // average depth of all player pixels (Topic 05, attempt 2)
        // count = how many player pixels were found (to know if someone is really there)
        private float CalculateBodyDepth(out int count)
        {
            double sum = 0;
            count = 0;
            for (int i = 0; i < depthData.Length; i++)
            {
                // only pixels that belong to a person and have a valid depth (0 = no reading)
                if (bodyIndexData[i] != 0xff && depthData[i] > 0)
                {
                    sum += depthData[i];
                    count++;
                }
            }
            return count > 0 ? (float)(sum / count) : 0;
        }

        // finds the hand in the mask and sets handDetected, handX and handForwardMm
        private void UpdateHandFromMask(float dBody, bool bodyFound)
        {
            // start with "no hand", only set to true if a hand is really found
            handDetected = false;
            handForwardMm = 0;

            if (bodyFound)
            {
                using (var contours = new VectorOfVectorOfPoint())
                {
                    // find all white blobs in the mask (outer contours only)
                    CvInvoke.FindContours(handMaskImg, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

                    // the biggest blob is the hand (so small noise or a second hand are ignored)
                    int best = -1;
                    double bestArea = MIN_HAND_AREA;
                    for (int i = 0; i < contours.Size; i++)
                    {
                        double area = CvInvoke.ContourArea(contours[i]);
                        if (area > bestArea) { bestArea = area; best = i; }
                    }

                    if (best != -1)
                    {
                        // hand x = middle of the bounding box, as a value from 0 to 1
                        System.Drawing.Rectangle box = CvInvoke.BoundingRectangle(contours[best]);
                        handX = (box.X + box.Width / 2.0) / depthFrameDescription.Width;
                        if (mirrorX) handX = 1 - handX;

                        // how far the hand is in front of the body
                        handForwardMm = dBody - AverageMaskDepth(box);
                        handDetected = true;
                    }
                }
            }

            // values in the window title, so I can calibrate the thresholds while standing in front of the Kinect
            Title = $"Fl0w3R | Kinect | dBody: {dBody:0} mm | hand: {(handDetected ? "yes" : "no")} | X: {handX:0.00} | forward: {handForwardMm:0} mm";

            OnHandInputUpdated();
        }

        // average depth of the hand pixels inside the bounding box
        // (adapted from CalculateAverageDepth, Topic 05)
        private float AverageMaskDepth(System.Drawing.Rectangle box)
        {
            byte[,,] mask = handMaskImg.Data;
            double sum = 0;
            int count = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    if (mask[y, x, 0] == 255) // only white pixels = hand
                    {
                        sum += depthData[y * depthFrameDescription.Width + x];
                        count++;
                    }
                }
            return count > 0 ? (float)(sum / count) : 0;
        }

        // Hand input
        // key M switches between mouse simulation and Kinect hand tracking
        // (also a backup if the Kinect doesn't work during a demo)
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.M) return;

            useMouseSimulation = !useMouseSimulation;
            handDetected = false;   // reset the hand when switching, so nothing is triggered by accident
            handForwardMm = 0;
            OnHandInputUpdated();
            Title = useMouseSimulation ? "Fl0w3R | mouse simulation" : "Fl0w3R | Kinect hand tracking";
        }

        // simulates the hand with the mouse:
        // mouse left/right = hand left/right, mouse up = hand pushed forward
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (!useMouseSimulation) return;

            Point p = e.GetPosition(this);
            handX = Math.Max(0, Math.Min(1, p.X / ActualWidth));                  // 0..1
            handForwardMm = (1 - p.Y / ActualHeight) * SIM_MAX_FORWARD_MM;        // bottom = 0, top = 600 mm
            handDetected = handForwardMm >= HAND_OFFSET_MM;                       // same rule as with the Kinect

            // debug values in the window title
            Title = $"Fl0w3R | Hand: {(handDetected ? "yes" : "no")} | X: {handX:0.00} | forward: {handForwardMm:0} mm";

            OnHandInputUpdated();
        }

        // Central place for the hand input: mouse and Kinect both end up here,
        // so the game logic does not care where the values come from.
        private void OnHandInputUpdated()
        {
            if (tiles == null) return; // window not fully loaded yet

            // 0. the hand only counts during the player's turn
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

            // 1. after choosing a tile: ignore everything until the hand is back at the body
            //    (otherwise one push could choose the same tile several times)
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

            // 3. which tile is under the hand (4 equal parts of the width), -1 = no hand
            int tileUnderHand = handDetected ? Math.Min((int)(handX * NUM_TILES), NUM_TILES - 1) : -1;

            // 4. as soon as the flower starts growing, the tile is locked
            //    (the hand moves a bit sideways when pushing, this should not switch the tile)
            if (lockedTile == -1 && tileUnderHand != -1 && growth > 0)
                lockedTile = tileUnderHand;

            // 5. hand pulled back before the flower was fully grown -> back to bud, tile is free again
            if (lockedTile != -1 && growth <= 0)
            {
                SetTileGrowth(lockedTile, 0);
                lockedTile = -1;
            }

            // highlight the locked tile, or else the tile under the hand
            hoveredTile = (lockedTile != -1) ? lockedTile : tileUnderHand;

            // 6. the locked flower grows with the hand; fully grown = the tile is chosen
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

            // debug text at the bottom of the game screen
            string layer = !handDetected ? "neutral" : (growth > 0 ? "grow" : "point");
            debugText.Text = $"Layer: {layer} | Tile: {hoveredTile} | Growth: {growth:P0}";
        }

        // Tiles 

        // the tile the hand points at gets a white frame, the others get darker;
        // if no hand is there, all tiles look normal
        private void UpdateTileHighlight()
        {
            for (int i = 0; i < NUM_TILES; i++)
            {
                bool isHovered = (i == hoveredTile);
                tiles[i].BorderBrush = isHovered ? Brushes.White : Brushes.Transparent;
                tiles[i].Opacity = (hoveredTile == -1 || isHovered) ? 1.0 : 0.5;
            }
        }

        // shows the flower frame that fits the growth (0..1) on one tile
        // e.g. growth 0.5 -> frame 2 of 0-4
        private void SetTileGrowth(int tile, double growth)
        {
            int frame = (int)Math.Round(growth * (flowerFrames.Length - 1));
            tileImages[tile].Source = flowerFrames[frame];
        }

        // all four tiles back to buds
        private void ResetAllTilesToBud()
        {
            for (int i = 0; i < NUM_TILES; i++)
                SetTileGrowth(i, 0);
        }

        // maps a value from one range to another (from class, Topic 05)
        private double Map(double value, double start1, double stop1, double start2, double stop2)
        {
            return (value - start1) / (stop1 - start1) * (stop2 - start2) + start2;
        }

        // keeps a value between 0 and 1
        private double Clamp01(double v)
        {
            return Math.Max(0, Math.Min(1, v));
        }

        // Game flow
        // Start -> Playback -> PlayerTurn -> (all correct) Playback with one more step / (wrong) GameOver

        // start button: new game with a random sequence of 2 steps
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            score = 0;
            sequence.Clear();
            for (int i = 0; i < 2; i++)
                sequence.Add(rng.Next(NUM_TILES)); // random tile 0-3

            ResetAllTilesToBud();
            hoveredTile = -1;
            UpdateTileHighlight();

            // switch from start screen to game screen
            startScreenGrid.Visibility = Visibility.Collapsed;
            gameOverGrid.Visibility = Visibility.Collapsed;
            gameScreenGrid.Visibility = Visibility.Visible;

            StartPlayback();
        }

        // "Play again": back to the start screen
        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            gameOverGrid.Visibility = Visibility.Collapsed;
            startScreenGrid.Visibility = Visibility.Visible;
            currentState = GameState.Start;
        }

        // starts the memorize phase: the sequence is played on the tiles (bloom -> pause -> bloom -> ...)
        private void StartPlayback()
        {
            currentState = GameState.Playback;
            playbackIndex = 0;          // always start from the first step
            playbackShowing = false;
            lockedTile = -1;
            waitingForNeutral = false;

            statusText.Text = $"Remember... Score: {score}";
            statusText.Foreground = Brushes.Orange;

            // short pause first, so the last chosen flower stays visible for a moment
            playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_START_DELAY);
            playbackTimer.Start();
        }

        // called by the timer: switches between "flower blooms" and "short pause"
        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (!playbackShowing)
            {
                // pause is over -> show the next step, or it's the player's turn
                if (playbackIndex >= sequence.Count)
                {
                    playbackTimer.Stop();
                    StartPlayerTurn();
                    return;
                }

                ResetAllTilesToBud();
                SetTileGrowth(sequence[playbackIndex], 1.0); // this step's flower blooms fully
                playbackShowing = true;
                playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_SHOW_SECONDS);
            }
            else
            {
                // blooming is over -> short pause, so the same tile twice in a row looks like two steps
                ResetAllTilesToBud();
                playbackShowing = false;
                playbackIndex++;
                playbackTimer.Interval = TimeSpan.FromSeconds(PLAYBACK_GAP_SECONDS);
            }
        }

        // the player repeats the sequence now
        private void StartPlayerTurn()
        {
            currentState = GameState.PlayerTurn;
            playerIndex = 0;
            lockedTile = -1;
            waitingForNeutral = true; // hand has to start at the body, so nothing is chosen by accident
            ResetAllTilesToBud();

            statusText.Text = $"Your turn! 0/{sequence.Count}";
            statusText.Foreground = Brushes.Lime;
        }

        // called once when a flower is fully grown = the player chose this tile
        private void OnTileConfirmed(int tile)
        {
            if (currentState != GameState.PlayerTurn) return;

            if (tile == sequence[playerIndex])
            {
                // correct -> next step
                playerIndex++;
                statusText.Text = $"Your turn! {playerIndex}/{sequence.Count}";

                if (playerIndex >= sequence.Count)
                {
                    // whole sequence correct -> next round with one more random step
                    score = sequence.Count;
                    sequence.Add(rng.Next(NUM_TILES));
                    StartPlayback();
                }
            }
            else
            {
                // wrong tile -> game over
                TriggerGameOver();
            }
        }

        // shows the game over screen with the score
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
