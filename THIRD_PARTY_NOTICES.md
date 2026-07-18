# Third-party notices

## VPC MongoosT-50CM3 visual template

The visual template used for Joydex's CM3 button map is adapted from the [VPC MongoosT-50CM3 Throttle Template](https://www.reddit.com/r/hotas/comments/o3pqnb/vpc_mongoost50cm3_throttle_template/) created by Reddit user u/axefan1.

The creator shared the source files and wrote:

> "Feel free to copy this template and swap out the device images and logical button mappings for your own devices!"

This attribution applies to the button-map asset in `src/Joydex.App/Assets/cm3-button-map.png` and the derived documentation image in `docs/images/joydex-button-map.png`.

## VIRPIL HID LED communication

Joydex's volatile LED feature-report implementation is informed by the Apache-2.0-licensed [VLEDCONTROL](https://github.com/Nereid42/VLEDCONTROL) project by Nereid42. Joydex contains a separately adapted implementation for its limited task-alert use case.

Joydex uses [HidSharp](https://github.com/SeekHisKingdom/HIDSharp), copyright 2010-2025 James F. Bellinger, under the Apache License 2.0, to open VIRPIL HID feature-report streams.
