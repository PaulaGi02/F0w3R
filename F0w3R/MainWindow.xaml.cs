using System;
using System.Collections.Generic;
using System.Linq;
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

using Microsoft.Kinect;
using NUI3D;

namespace T3_DrawingInAir
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

        private int drawingSpaceMin = 1500; // mm 
        private int drawingSpaceMax = 2000; // mm 

        private ColorFrameManager colorFrameManager;

        public MainWindow()
        {
            InitializeComponent();
        }



        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            System.Console.WriteLine("window loaded");

            sensor = KinectSensor.GetDefault(); // get the default Kinect sensor 

            // Open the reader for the depth frames
            DepthFrameReader depthFrameReader = sensor.DepthFrameSource.OpenReader();

            // register an event handler for FrameArrived 
            depthFrameReader.FrameArrived += DepthFrameReader_FrameArrived;

            sensor.Open();

            // allocate storage for depth data
            depthFrameDescription = sensor.DepthFrameSource.FrameDescription;
            // 16 - bit unsigned integer per pixel
            depthData = new ushort[depthFrameDescription.LengthInPixels];

            depthPixels = new byte[depthFrameDescription.LengthInPixels * bytesPerPixel];

            depthImageBitmap = new WriteableBitmap(
                                       depthFrameDescription.Width, // 512 
                                       depthFrameDescription.Height, // 424
                                       96, 96, PixelFormats.Bgr32, null);
            kinectDepthImage.Source = depthImageBitmap; // assign the bitmap to WPF image

            colorFrameManager = new ColorFrameManager();
            colorFrameManager.Init(sensor, kinectColorImage);

        }

        private void DepthFrameReader_FrameArrived(object sender, DepthFrameArrivedEventArgs e)
        {
            // using statement automatically takes care of disposing of 
            // the DepthFrame object when you are done using it
            using (DepthFrame depthFrame = e.FrameReference.AcquireFrame())
            {
                if (depthFrame == null) return;

                depthFrame.CopyFrameDataToArray(depthData);

                // depth data to pixels
                ushort minDepth = depthFrame.DepthMinReliableDistance; // 500 
                ushort maxDepth = depthFrame.DepthMaxReliableDistance; // 4500 

                /*
                for (int i = 0; i < depthData.Length; ++i)
                {
                    ushort depth = depthData[i];

                    // drawing space
                    if (depth < drawingSpaceMax && depth > drawingSpaceMin)
                    {
                        depthPixels[bytesPerPixel * i] = 0;
                        depthPixels[bytesPerPixel * i + 1] = 255; // green 
                        depthPixels[bytesPerPixel * i + 2] = 0;
                    }
                    // class exercise 3: another drawing space  
                    else if (depth > 2000 && depth < 2500)
                    {
                        depthPixels[4 * i] = 0;
                        depthPixels[4 * i + 1] = 0;
                        depthPixels[4 * i + 2] = 255; // red 
                    }

                    // class exercise 2: comment out the following else block 
                    else
                    {
                        byte depthByte = (byte)((depth - minDepth) * 255.0 / (maxDepth - minDepth));
                        depthByte = (byte)(255 - depthByte);

                        depthPixels[bytesPerPixel * i] = depthByte;
                        depthPixels[bytesPerPixel * i + 1] = depthByte;
                        depthPixels[bytesPerPixel * i + 2] = depthByte;
                    }
                }*/

                // class exercise 4
                for (int x = 0; x < depthFrameDescription.Width; x++)
                    for (int y = 0; y < depthFrameDescription.Height; y++)
                    {
                        int index = y * depthFrameDescription.Width + x;
                        ushort depth = depthData[index];

                        // class exercise 
                        if (depth > drawingSpaceMin && depth < drawingSpaceMax) // mm
                        {
                            // if (y < depthFrameDescription.Height / 2)
                            if (x < depthFrameDescription.Width / 2)
                            {
                                depthPixels[bytesPerPixel * index] = 0;
                                depthPixels[bytesPerPixel * index + 1] = 255; // green 
                                depthPixels[bytesPerPixel * index + 2] = 0;
                            }
                            else
                            {
                                depthPixels[bytesPerPixel * index] = 0;
                                depthPixels[bytesPerPixel * index + 1] = 0;
                                depthPixels[bytesPerPixel * index + 2] = 255; // red
                            }
                        }
                    }


                depthImageBitmap.WritePixels(
                  new Int32Rect(0, 0, depthFrameDescription.Width, depthFrameDescription.Height),
                  depthPixels, // BGR32 pixel data
                               // stride: width in bytes of a single row of pixel data
                  depthFrameDescription.Width * bytesPerPixel, 0);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            Array.Clear(depthPixels, 0, depthPixels.Length);
        }
    }
}
