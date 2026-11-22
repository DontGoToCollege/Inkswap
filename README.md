# Inkswap

Translate manga, manhwa and manhua pages on your own PC, offline. Inkswap finds the speech bubbles,
reads the text, translates it, erases the original lettering and letters the translation back into
the bubble. You can then fix anything by hand and save the finished pages.

- **Languages:** Japanese, Korean, Chinese, Spanish and English, in any direction.
- **Side-by-side pane:** every bubble is numbered on the page and listed on the right with its
  original text and its translation, each labelled with its language, both editable.
- **Lettering:** matched to the original's colour, sized to fill the bubble, in a comic font
  (Comic Neue and Bangers are built in, or use any font installed on your PC). Per-bubble options set
  the font, size, colour, bold, outline, what to erase and whether to fill the whole bubble.
- **Offline:** the AI (a Qwen3-VL vision model running on llama.cpp) runs on your graphics card.
  Nothing is sent anywhere.

## Download

Get the latest `Inkswap-<version>.exe` from the [Releases page](https://github.com/DontGoToCollege/Inkswap/releases).
It's a single file with nothing to install (Windows 10/11, 64-bit).

The first time you start it, Inkswap asks which AI model to download (once, from Hugging Face and
GitHub, checked against fixed fingerprints):

| Model | Download | Graphics memory |
|---|---|---|
| Standard (Qwen3-VL 4B) | 3.4 GB | about 5 GB |
| Best (Qwen3-VL 8B) | 5.8 GB | about 8 GB |

It runs on any graphics card with Vulkan drivers (NVIDIA, AMD, Intel). If a model doesn't fit in the
card's free memory, the part that doesn't fit runs on the processor: it still works, just slower
(the Best model reads a page in about 27 seconds on an 8 GB laptop card, the Standard one in about 7). The model is saved in
`Inkswap-Data` next to the exe, so copy that folder along with the exe to move it to another PC or
a USB stick.

## How to use it

1. **Open images** (or drop image files on the window). Each page appears on the left.
2. Pick the language **From** and **To**.
3. Press **Translate page** (or **Translate all pages**). The boxes and texts appear as it works.
4. Check the pane on the right. Edit any original text or translation; **Re-read** and
   **Re-translate** redo one bubble. Drag on the page to add a box the AI missed.
5. Adjust the boxes if needed. Each bubble has two:
   - a green **text box**: where the translation is lettered. It stays where you put it.
   - an orange **erase area**: what gets removed from the original. Inside a speech bubble it
     clears everything up to the outline, so it doesn't have to fit the text exactly.

   Pick which ones to move in the **Boxes** menu (or press **E** to switch). Drag a box to move it,
   drag any corner to resize it, use the arrow keys to nudge it (hold Shift for 10 pixels) and press
   Delete to remove it. **Fit to bubble again** (under Lettering) puts a text box back.
6. **Save page** saves a PNG; **Save all** saves every translated page into a folder you pick.

Switch the view to **Original** to compare, and set **Boxes** to **Hidden** to see the clean result.

## Limits

- Erasing is cleanest on plain speech bubbles. Text drawn over detailed art is filled in from the
  surrounding colours, which can leave a smudge. Set that bubble's **Erase** to **Nothing** and
  fix it in an image editor if needed.
- The original font can't be copied exactly (Japanese, Korean and Chinese fonts have no Latin
  letters), so Inkswap matches the colour, size and placement and uses a comic lettering font.
- Translated text is always lettered horizontally.
- Work isn't saved between sessions. Save your pages before closing.
- **Save all** can't save into Windows system folders such as AppData; pick a normal folder like
  Pictures or Documents.

## Building

Needs the .NET 10 SDK. `.\publish.ps1` builds `Inkswap.exe`, runs its self-check and archives the
version in `Versions\Archive`.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
