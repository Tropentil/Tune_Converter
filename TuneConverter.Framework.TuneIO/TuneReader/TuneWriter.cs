﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TuneConverter.Framework.TuneIO.TuneReader;

/// <summary>
/// Writes tune to a .txt file
/// </summary>
public class TuneWriter
{
    private static readonly string _notesPath = "C:/Users/Isaac/source/repos/TuneConverter/TuneConverter.Framework.PageImageIO/InputNotes/";

    /// <summary>
    /// Writes tune to a .txt file
    /// </summary>
    /// <param name="lines"></param>
    public void WriteFile(List<string> lines)
    {
        TextWriter tw = new StreamWriter(_notesPath + lines[0] + ".txt");

        foreach (var s in lines)
            tw.WriteLine(s);

        tw.Close();
    }
}
