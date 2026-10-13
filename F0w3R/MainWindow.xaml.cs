using Microsoft.Kinect;
using Emgu.CV;
using Emgu.CV.Structure;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;


// Fl0w3R - SM3603 Assignment 1
// SOURCE LEGEND (used in the comments below)
// [CLASS]    copied from the SM3603 class examples (topic + slide number)
// [ADAPTED]  based on the SM3603 class examples, changed as described in the comment
// [ME]       written by me
// [WEB]      web page I used to understand the concept (further reading, not copied)

namespace Fl0w3R
{
    public partial class MainWindow : Window
    {
        // ---------- Kinect ----------
        // [CLASS] Topic 03, slides 23 and 30-31 (sensor, depth data, depth bitmap)
        private KinectSensor sensor;
        private BodyIndexFrameReader bodyIndexFrameReader;  // [CLASS] Topic 05, slide 9
        private FrameDescription depthFrameDescription;  // size of the depth frame (512 x 424)
        private ushort[] depthData;                      // raw depth per pixel in mm
        private byte[] bodyIndexData;                    // [CLASS] Topic 05, slide 8-9: 255 = background, 0-5 = a detected person
        private int bytesPerPixel = 4;                   // BGR32 image: blue, green, red, unused
        private byte[] depthPixels;                      // the colored depth image I show on screen
        private WriteableBitmap depthImageBitmap;        // WPF bitmap that displays depthPixels

        // ---------- Flower frames ----------
        // [ME] 5 images of the growing flower, frame 0 = bud, frame 4 = fully grown
        private const int NUM_FLOWER_FRAMES = 5;
        private BitmapImage[] flowerFrames = new BitmapImage[NUM_FLOWER_FRAMES];

        // ---------- Hand segmentation ----------
        // [ADAPTED] idea from Topic 05, slides 29-34: everything of the player that is clearly in front of the body is the hand
        //           Image<Gray, byte> and contour detection from Topic 04 (slides 20, 39) and Topic 05 (slide 20)
        private Image<Gray, byte> handMaskImg;         // black and white image: white = hand pixels
        private const double HAND_OFFSET_MM = 250;     // [ADAPTED] Topic 05 slide 34 uses offset = 200 mm; tuned to 250 mm
        private const int MIN_BODY_PIXELS = 3000;      // [ME] if fewer player pixels are found, nobody is really there
        private const double MIN_HAND_AREA = 500;      // [ADAPTED] Topic 05 slide 20 filters small contours (50*50); tuned to 500 px
        private const double REACH_MM = 400;           // [ME] hand +-400 mm from the body center covers all four tiles
        private const double DEPTH_FOCAL_PX = 365;     // [ME] approx. focal length of the Kinect v2 depth camera (pixels)
                                                       // [WEB] Lachat et al. 2015, "First Experiences with Kinect v2 Sensor for Close Range 3D Modelling" (Table 9: ~366 px)

        // ---------- Hand input ----------
        // [ME] the three values the game needs from the hand
        private bool handDetected = false;   // is there a hand in front of the body?
        private double handX = 0.5;          // hand position from 0 (left) to 1 (right)
        private double handForwardMm = 0;    // how far the hand is in front of the body (mm)

        // ---------- Tiles ----------
        // [ME]
        private const int NUM_TILES = 4;
        private Border[] tiles;          // the four colored tiles (so I can loop over them)
        private Image[] tileImages;      // the flower image inside each tile
        private int hoveredTile = -1;    // tile the hand currently points at, -1 = none

        // ---------- Layer logic ----------
        // [ME] the depth of the hand relative to the body decides what happens
        // neutral (< 250 mm) -> point (250-300 mm) -> grow (300-450 mm) -> chosen (450 mm)
        private const double GROW_START_MM = 300;  // the flower starts growing here (small gap after pointing)
        private const double CONFIRM_MM = 450;     // flower fully grown = tile is chosen
        private int lockedTile = -1;               // while a flower grows, its tile stays locked
        private bool waitingForNeutral = false;    // after choosing, the hand has to go back to the body first

        // ---------- Game state ----------
        // [ME] game states and sequence logic
        // [WEB] https://learn.microsoft.com/en-us/dotnet/api/system.enum?view=net-10.0
        private enum GameState { Start, Playback, PlayerTurn, GameOver }
        private GameState currentState = GameState.Start;

        private List<int> sequence = new List<int>(); // the order the player has to remember (tile numbers 0-3)
        private int playbackIndex = 0;                // which step of the sequence is shown right now
        private bool playbackShowing = false;         // true = a flower is blooming, false = short pause
        private int playerIndex = 0;                  // which step the player has to enter next
        private int score = 0;
        private Random rng = new Random();            // [ADAPTED] Random as used in Topic 04 slide 58 / Topic 05 slide 22
        private DispatcherTimer playbackTimer;        // [ME] timer that plays the sequence step by step
                                                      // [WEB] https://learn.microsoft.com/en-us/dotnet/api/system.windows.threading.dispatchertimer

        private const double PLAYBACK_START_DELAY = 1.0;  // [ME] pause before the sequence starts
        private const double PLAYBACK_SHOW_SECONDS = 1.0; // [ME] how long one flower blooms
        private const double PLAYBACK_GAP_SECONDS = 0.4;  // [ME] pause between steps, so the same tile twice is visible as two steps


        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // [ME] flower images: flower1.png (bud) ... flower5.png (fully grown) from Assets folder
            // [WEB] https://codemia.io/knowledge-hub/path/setting_wpf_image_source_in_code
            for (int i = 0; i < NUM_FLOWER_FRAMES; i++)
                flowerFrames[i] = new BitmapImage(new Uri($"pack://application:,,,/Assets/flower{i + 1}.png", UriKind.Absolute));

            // [ME] put the tiles into arrays so I can handle them in loops
            tiles = new Border[] { tile0, tile1, tile2, tile3 };
            tileImages = new Image[] { tileImg0, tileImg1, tileImg2, tileImg3 };
            ResetAllTilesToBud();

            // [ME] timer for the memorize phase
            playbackTimer = new DispatcherTimer();
            playbackTimer.Tick += PlaybackTimer_Tick;

            // [ADAPTED] Kinect setup from Topic 03 (slides 23, 30-31) and Topic 05 (slide 9)
            // changed: all arrays are created before the readers are opened, the body index array
            // is sized from the body index source, and the hand mask image is created here [ME]
            sensor = KinectSensor.GetDefault();
            depthFrameDescription = sensor.DepthFrameSource.FrameDescription;
            depthData = new ushort[depthFrameDescription.LengthInPixels];
            depthPixels = new byte[depthFrameDescription.LengthInPixels * bytesPerPixel];
            bodyIndexData = new byte[sensor.BodyIndexFrameSource.FrameDescription.LengthInPixels];
            handMaskImg = new Image<Gray, byte>(depthFrameDescription.Width, depthFrameDescription.Height);

            // [ADAPTED] Topic 03 slide 31; changed: the same bitmap is shown on the start screen and on the game screen
            depthImageBitmap = new WriteableBitmap(
                depthFrameDescription.Width, depthFrameDescription.Height,
                96, 96, PixelFormats.Bgr32, null);
            kinectDepthImage.Source = depthImageBitmap;
            kinectDepthImageStart.Source = depthImageBitmap;

            // [CLASS] Topic 03 slide 23: depth reader, runs DepthFrameReader_FrameArrived for every new depth frame
            DepthFrameReader depthFrameReader = sensor.DepthFrameSource.OpenReader();
            depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;

            // [CLASS] Topic 05 slide 9: body index reader, tells me which pixels belong to a person
            bodyIndexFrameReader = sensor.BodyIndexFrameSource.OpenReader();
            bodyIndexFrameReader.FrameArrived += BodyIndexFrameReader_FrameArrived;

            sensor.Open();
        }

        // ---------- Kinect frames ----------

        // [CLASS] Topic 05 slide 10 (unchanged)
        // saves the newest body index frame, so the depth handler knows which pixels are a person
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
        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            // [CLASS] Topic 03 slides 24 and 30: acquire the frame and copy the depth data
            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame == null) return;

                depthFrame.CopyFrameDataToArray(depthData);

                // [CLASS] Topic 03 slide 39
                ushort minDepth = depthFrame.DepthMinReliableDistance; // 500 mm
                ushort maxDepth = depthFrame.DepthMaxReliableDistance; // 4500 mm

                // 1. body depth = average depth of all player pixels
                //    [ADAPTED] Topic 05 slides 36-37 (Attempt 2); the hand barely changes the average
                float dBody = CalculateBodyDepth(out int bodyPixelCount, out double bodyCenterX);
                bool bodyFound = bodyPixelCount > MIN_BODY_PIXELS;     // [ME]
                float handThreshold = dBody - (float)HAND_OFFSET_MM;   // [ADAPTED] Topic 05 slides 30, 34: d_hand < d_body - offset

                // 2. build the hand mask and the colored depth image in one loop
                //    [ADAPTED] Topic 05 slides 34 and 38-39 (hand segmentation) and Topic 03 slide 52 (x/y loop, index)
                //    changed: hand = yellow and body = green (class: green/blue), extra checks bodyFound and depth > 0,
                //    and the mask is written directly into the Emgu image [ME] instead of
                //    BitmapSource -> Bitmap -> Image as in class (Topic 04 slide 33)
                byte[,,] mask = handMaskImg.Data;
                int width = depthFrameDescription.Width;

                for (int y = 0; y < depthFrameDescription.Height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;   // [CLASS] Topic 03 slide 52: 2D position -> position in the 1D array
                        ushort depth = depthData[index];

                        bool isPerson = bodyIndexData[index] != 0xff;
                        bool isHand = bodyFound && isPerson && depth > 0 && depth < handThreshold;
                        mask[y, x, 0] = isHand ? (byte)255 : (byte)0; // white = hand

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
                            // [CLASS] Topic 03 slides 39 and 46: background as gray depth image, near = bright, far = dark
                            byte depthByte = (byte)((depth - minDepth) * 255.0 / (maxDepth - minDepth));
                            depthByte = (byte)(255 - depthByte);
                            depthPixels[p] = depthByte; depthPixels[p + 1] = depthByte; depthPixels[p + 2] = depthByte;
                        }
                    }

                // [CLASS] Topic 03 slide 31: show the colored image on screen
                depthImageBitmap.WritePixels(
                    new Int32Rect(0, 0, width, depthFrameDescription.Height),
                    depthPixels, width * bytesPerPixel, 0);

                // 3. [ME] get the hand values from the mask and pass them to the game
                UpdateHandFromMask(dBody, bodyFound, bodyCenterX);
            }
        }

        // [ADAPTED] Topic 05 slides 36-37 (Attempt 2): average depth of all player pixels
        // changed: also skips invalid pixels (depth 0), returns the pixel count, and computes the
        // average x position (body center) [ME]
        private float CalculateBodyDepth(out int count, out double bodyCenterX)
        {
            double sum = 0;
            double sumX = 0;
            count = 0;
            int width = depthFrameDescription.Width;
            for (int i = 0; i < depthData.Length; i++)
            {
                // only pixels that belong to a person and have a valid depth (0 = no reading)
                if (bodyIndexData[i] != 0xff && depthData[i] > 0)
                {
                    sum += depthData[i];
                    sumX += i % width;   // [ME] x position of this pixel
                    count++;
                }
            }
            bodyCenterX = count > 0 ? sumX / count : width / 2.0;
            return count > 0 ? (float)(sum / count) : 0;
        }

        // finds the hand in the mask and sets handDetected, handX and handForwardMm.
        // hand x is measured relative to the body center (relative interaction space, Topic 05 slide 5),
        // so the player doesn't have to reach to the image edge for the outer tiles
        private void UpdateHandFromMask(float dBody, bool bodyFound, double bodyCenterX)
        {
            // [ME] start with "no hand", only set to true if a hand is really found
            handDetected = false;
            handForwardMm = 0;

            if (bodyFound)
            {
                // [CLASS] Topic 04 slides 39 and 48, Topic 05 slide 20: find all outer contours
                using (var contours = new VectorOfVectorOfPoint())
                {
                    CvInvoke.FindContours(handMaskImg, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);

                    // [ADAPTED] Topic 05 slide 20 filters out small contours with ContourArea;
                    // [ME] here only the biggest contour is kept and counts as the hand
                    // (so small noise or a second hand are ignored)
                    // [WEB] https://docs.opencv.org/4.13.0/dd/d49/tutorial_py_contour_features.html
                    // [WEB] https://en.morzel.net/posts/detecting-a-drone-opencv-in-dotnet-for-beginners-emgu-cv-3-visual-studio-2017-part-3/
                    int best = -1;
                    double bestArea = MIN_HAND_AREA;
                    for (int i = 0; i < contours.Size; i++)
                    {
                        double area = CvInvoke.ContourArea(contours[i]);
                        if (area > bestArea) { bestArea = area; best = i; }
                    }

                    if (best != -1)
                    {
                        // [CLASS] Topic 04 slides 49 and 52: axis-aligned bounding box
                        System.Drawing.Rectangle box = CvInvoke.BoundingRectangle(contours[best]);
                        double handCenterX = box.X + box.Width / 2.0;

                        // [ME] reach in pixels depends on the distance: the further away, the fewer pixels
                        // (pinhole camera model: size in pixels = real size * focal length / distance)
                        double reachPx = REACH_MM * DEPTH_FOCAL_PX / dBody;

                        // [ME] body center -> 0.5, body center +- reach -> 0 / 1
                        handX = Clamp01(0.5 + (handCenterX - bodyCenterX) / (2 * reachPx));

                        // [ME] how far the hand is in front of the body
                        handForwardMm = dBody - AverageMaskDepth(box);
                        handDetected = true;
                    }
                }
            }

            OnHandInputUpdated();
        }

        // [ADAPTED] CalculateAverageDepth from Topic 05 slide 26 (and Topic 04 slide 55):
        // average depth of the hand pixels inside the bounding box
        // changed: reads the mask image directly instead of a converted binary image, loops rows first,
        // sums in double
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

        // ---------- Hand input -> tiles ----------

        // [ME] whole method (layer logic): turns the three hand values into
        // which tile, how far the flower has grown, and when a tile is chosen
        private void OnHandInputUpdated()
        {
            // the hand only counts during the player's turn
            if (currentState != GameState.PlayerTurn) return;

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
                return;
            }

            // 2. how far the flower has grown: 0 = bud, 1 = fully grown (Map from Topic 05 slide 28)
            // [WEB] https://makecode.microbit.org/reference/pins/map
            double growth = Clamp01(Map(handForwardMm, GROW_START_MM, CONFIRM_MM, 0, 1));

            // 3. which tile is under the hand (4 equal parts of the width), -1 = no hand
            int tileUnderHand = handDetected ? Math.Min((int)(handX * NUM_TILES), NUM_TILES - 1) : -1;

            // 4. as soon as the flower starts growing, the tile is locked
            //    (the hand moves a bit sideways when going forward, this shouldn't switch the tile)
            if (lockedTile == -1 && tileUnderHand != -1 && growth > 0)
                lockedTile = tileUnderHand;

            // 5. hand pulled back before the flower was fully grown -> reset: back to bud, tile free again
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
        }

        // [ME] the tile the hand points at gets a white frame, the others get darker;
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

        // [ME] shows the flower frame that fits the growth (0..1) on one tile, e.g. growth 0.5 -> frame 2 of 0-4
        private void SetTileGrowth(int tile, double growth)
        {
            int frame = (int)Math.Floor(growth * (NUM_FLOWER_FRAMES - 1)); 
            tileImages[tile].Source = flowerFrames[frame];
        }

        // [ME] all four tiles back to buds
        private void ResetAllTilesToBud()
        {
            for (int i = 0; i < NUM_TILES; i++)
                SetTileGrowth(i, 0);
        }

        // [ADAPTED] Map() from Topic 05 slide 28 (changed from float to double)
        // maps a value from one range to another
        private double Map(double value, double start1, double stop1, double start2, double stop2)
        {
            return (value - start1) / (stop1 - start1) * (stop2 - start2) + start2;
        }

        // [ME] keeps a value between 0 and 1
        // [WEB] https://code-maze.com/csharp-math/
        private double Clamp01(double v)
        {
            return Math.Max(0, Math.Min(1, v));
        }

        // ---------- Game flow ----------
        // [ME] all methods below (Simon-says game flow):
        // Start -> Playback -> PlayerTurn -> (all correct) Playback with one more step / (wrong) GameOver

        // start button: new game with a random sequence of 2 steps
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            score = 0;
            sequence.Clear();
            for (int i = 0; i < 2; i++)
                sequence.Add(rng.Next(NUM_TILES)); // random tile 0-3

            ResetAllTilesToBud();

            // switch from start screen to game screen
            // [WEB] https://www.c-sharpcorner.com/article/visibilitycollapsed-vs-visibilityhidden-in-wpf
            startScreenGrid.Visibility = Visibility.Collapsed;
            gameScreenGrid.Visibility = Visibility.Visible;

            StartPlayback();
        }

        // back to the start screen
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

            // no tile is highlighted while the sequence is shown
            hoveredTile = -1;
            UpdateTileHighlight();

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
            waitingForNeutral = true; // hand has to start at the body

            statusText.Text = $"Your turn! 0/{sequence.Count}";
            statusText.Foreground = Brushes.Lime;
        }

        // once when a flower is fully grown = the player chose this tile
        private void OnTileConfirmed(int tile)
        {
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
                // wrong tile -> game over screen with the score
                currentState = GameState.GameOver;
                finalScoreText.Text = $"Score: {score}";
                gameScreenGrid.Visibility = Visibility.Collapsed;
                gameOverGrid.Visibility = Visibility.Visible;
            }
        }
    }
}
