﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TuneConverter.Framework.PageImageIO.ImageComponents;

public enum TuneType
{
    /// <summary>
    /// The page width for a Polka (13 notes including spaces)
    /// </summary>
    Polka = 13 * 10,

    /// <summary>
    /// The page width for a Slip-Jig (14 notes including spaces)
    /// </summary>
    Slipjig = 14 * 20,

    /// <summary>
    /// The page width for a Jig (17 notes including spaces)
    /// </summary>
    Jig = 17 * 30,

    /// <summary>
    /// The page width for a Slide (17 notes including spaces)
    /// </summary>
    Slide = 17 * 40,

    /// <summary>
    /// The page width for a Reel (21 notes including spaces)
    /// </summary>
    Reel = 21 * 50,

    /// <summary>
    /// The page width for a Hornpipe (21 notes including spaces)
    /// </summary>
    Hornpipe = 21 * 60,
    Barndance = 21 * 70,
    Fling = 21 * 80,

    /// <summary>
    /// The page width for a Waltz (29 notes including spaces)
    /// </summary>
    Waltz = 29 * 90,

    /// <summary>
    /// The page width for a Mazurka (29 notes including spaces)
    /// </summary>
    Mazurka = 29 * 100
}
