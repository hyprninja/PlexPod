Tiny generated test tones (0.3 s sine, stereo), made with ffmpeg 9.0.2:

    ffmpeg -f lavfi -i "sine=frequency=440:duration=0.3:sample_rate=44100" -ac 2 -sample_fmt s16 -c:a flac tone-16-44.flac
    ffmpeg -f lavfi -i "sine=frequency=440:duration=0.3:sample_rate=44100" -ac 2 -c:a libmp3lame -b:a 64k tone.mp3
    ffmpeg -f lavfi -i "sine=frequency=440:duration=0.3:sample_rate=44100" -ac 2 -c:a aac -b:a 64k tone.m4a

Tests copy them to a temp folder and make them messy (duplicate tags, glued ID3, extra pictures) before running the tag writer.
