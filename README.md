# Snip

Press **Print Screen**. The editor opens with a shot of the screen your mouse is on.

1. **Crop**: drag a box around the part you want. The tool then changes to Pen.
2. **Pen**: draw circles or arrows. Pick a colour and Thin, Medium or Thick.
3. **Arrow** (A), **Box** (R), **Circle** (O): drag to draw one. For an arrow, drag from the tail to the tip.
   - The new shape gets white handles. Drag a handle to turn or resize it. Drag the shape's line to move it.
   - To change a shape later, pick Arrow, Box or Circle and drag its line or handles.
   - With a shape selected, a colour or size click changes that shape. **Delete** (Del) removes it.
4. **Undo** (Ctrl+Z) removes the last mark, change or crop.
5. **Done** (Enter) saves a PNG to Downloads as `Snip <date> <time>.png`. It also copies the image, so you can paste it (Ctrl+V) into a chat.
6. **Cancel** (Esc) closes without saving.

Snip runs in the background. Its icon is near the clock. Right-click it to take a shot, open Downloads, or quit.

## Start with Windows
Put a shortcut to `Snip.exe` in the Startup folder. To open that folder, press Win+R, type `shell:startup`, press Enter.

## If Print Screen opens the Windows Snipping Tool
Go to Settings > Accessibility > Keyboard and turn off "Use the Print screen key to open screen capture". Snip shows a reminder if this is on.

## Rebuild after a code change
Double-click `build.cmd`. It uses the C# compiler that comes with Windows. Quit Snip first.

## Test
`powershell -ExecutionPolicy Bypass -File test\e2e-test.ps1` starts Snip, presses Print Screen, crops, draws, undoes, saves, and checks the PNG in Downloads. It leaves the test PNG in Downloads.

## Errors
Problems are written to `%TEMP%\snip-error.log`.
