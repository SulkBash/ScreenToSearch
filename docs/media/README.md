# Walkthrough image credits

The animated walkthrough uses a cropped screenshot of [“Pillars of Creation”](https://en.wikipedia.org/w/index.php?title=Pillars_of_Creation&oldid=1381725509) by Wikipedia contributors. Wikipedia's article text is available under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). The screenshot was cropped and combined with reconstructed interface elements and captions; the Wikipedia-derived portions of `wikipedia-reference.png`, `illustrated-walkthrough.gif`, and `screen-to-search-demo.mp4` are distributed under CC BY-SA 4.0.

The 1995 Hubble photograph shown in the article is credited to [NASA/ESA, Jeff Hester and Paul Scowen (Arizona State University)](https://esahubble.org/images/opo9544a/).

The walkthrough is an animation of the Windows interface, not a recording. To regenerate the MP4 and GIF, install Pillow and FFmpeg (including the `libx264` encoder), then run `python tools/generate_readme_demo.py` from the repository root. Use `--preview` to render a contact sheet without generating the video.
