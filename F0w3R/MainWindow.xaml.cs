using Microsoft.Kinect;
using NUI3D;
using System;
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
        private const int SMOOTHING_WINDOW = 5;

        // flower frame images loading
        private BitmapImage[] flowerFrames = new BitmapImage[num_zo];

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine("window loaded");
            sensor = KinectSensor.GetDefault(); // get the default Kinect sensor 

            //existing depth reader setup
            // Open the reader for the depth frames
            DepthFrameReader depthFrameReader = sensor.DepthFrameSource.OpenReader();
            // register an event handler for FrameArrived 
            depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;

            //bodyindex Reader setup
            bodyIndexFrameReader = sensor.BodyIndexFrameSource.OpenReader();
            bodyIndexFrameReader.FrameArrived += BodyIndexFrameReader_FrameArrived;
            bodyIndexFrameDescription = sensor.BodyIndexFrameSource.FrameDescription;

            bodyIndexData = new byte[bodyIndexFrameDescription.LengthInPixels];

            sensor.Open();


            //old code
            depthFrameDescription = sensor.DepthFrameSource.FrameDescription;
            depthData = new ushort[depthFrameDescription.LengthInPixels];
            depthPixels = new byte[depthFrameDescription.LengthInPixels * bytesPerPixel];

            depthImageBitmap = new WriteableBitmap(
                                       depthFrameDescription.Width, // 512 
                                       depthFrameDescription.Height, // 424
                                       96, 96, PixelFormats.Bgr32, null);
            kinectDepthImage.Source = depthImageBitmap; // assign the bitmap to WPF image

            colorFrameManager = new ColorFrameManager();
            colorFrameManager.Init(sensor, kinectColorImage);

            LoadFlowerFrames();

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

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            Array.Clear(depthPixels, 0, depthPixels.Length);
        }
    }
}