**Fl0w3R
SM3603 Assignment 1 – Depth-based interaction with Kinect v2** 

------------------------------------------------------------
ABOUT
------------------------------------------------------------
Fl0w3R is a memory game played with one hand.
Four coloured tiles each hold a flower-bud. The game lets the
flowers bloom in a random order, and the player repeats that
order. Every round adds one flower; one wrong flower ends the game.

A tile is chosen with depth: the player points at a tile with
the hand and pushes the hand forward. The flower grows with the
push and the tile is chosen once the flower is fully grown.
Hand position and push distance are measured relative to the
player's own body, so it works wherever the player stands.

Video: <YouTube link>

------------------------------------------------------------
REQUIREMENTS
------------------------------------------------------------
- Windows 10/11
- Kinect for Windows SDK 2.0 (drivers + runtime)
- Kinect v2 sensor connected to a USB 3.0 port
- To build from source: Visual Studio 2022, .NET Framework 4.8

------------------------------------------------------------
HOW TO RUN
------------------------------------------------------------
1. Extract the zip file first (do not run the app from inside the zip).
2. Run: F0w3R\F0w3R\bin\Debug\F0w3R.exe
3. Windows SmartScreen may show "Windows protected your PC" because
   the app is not signed. Click "More info" -> "Run anyway".

To build from source: open F0w3R\F0w3R.sln in Visual Studio,
Build -> Rebuild Solution, then press F5. The Emgu CV native DLLs
are stored in F0w3R\F0w3R\libs and copied to bin\Debug on build.

------------------------------------------------------------
HOW TO PLAY
------------------------------------------------------------
Stand about 2 m in front of the sensor (body visible = green in
the Kinect view, hand = yellow). Click "Start".

1. Watch:  the flowers bloom one after another. Remember the order.
2. Point:  raise your hand in front of your body. The tile in front
           of it lights up. Move left/right to switch tiles.
3. Push:   move your hand straight towards the screen. The flower
           grows; fully grown = tile chosen.
4. Return: bring your hand back to your body before the next flower.

------------------------------------------------------------
TROUBLESHOOTING
------------------------------------------------------------
- "Unable to load DLL 'cvextern'": the folders x86 and x64 must be
  next to F0w3R.exe in bin\Debug. Extract the full zip, or Rebuild.
- Nothing reacts to the hand: check that the Kinect is on USB 3.0
  and that you appear green in the Kinect view on the start screen.

------------------------------------------------------------
CREDITS
------------------------------------------------------------
- Kinect setup, depth/body index handling, hand segmentation idea,
  contour detection and Map(): adapted from SM3603 class examples
  (Topics 03, 04, 05).
- Libraries: Microsoft Kinect for Windows SDK 2.0, Emgu CV 4.4.0.
- Flower images: made by me
