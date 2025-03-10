﻿using Emgu.CV;
using Emgu.CV.CvEnum;
using TuneConverter.Framework.PageImageIO.ImageComponents;
using TuneConverter.Framework.TuneComponents.TuneComponents;
using System.Drawing;
using Emgu.CV.Structure;
using TuneConverter.Framework.TuneComponents.Types;
using System.Collections.Generic;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Linq;
using TuneConverter.Framework.PageComponents;

namespace TuneConverter.Framework.PageImageIO.ImageBuilder;

/// <summary>
/// A class responsible for assembling pages of a tune, combining various components into a structured format.
/// </summary>
public class PageAssembler
{
    private const int DefaultNoteHeight = 80;
    private const int DefaultNoteWidth = 60;
    private const int DefaultNoteGap = 16;
    private const int DefaultNoteSpace = DefaultNoteWidth + DefaultNoteGap;
    private const int linkHeight = 141;
    private const int extraGap = 120;
    private const double A4Width = 210;
    private const double A4Height = 297;
    private const FontFace DefaultFont = FontFace.HersheyTriplex;

    private static readonly Gray White = new(255);
    private static readonly ListComparer<NoteGroup> Comparer = new();
    private static readonly ConcurrentDictionary<List<NoteGroup>, Image<Gray, byte>> BarCache = new(Comparer);
    private static readonly ConcurrentDictionary<int, List<Image<Gray, byte>>> PartsDict = new();
    private static readonly ConcurrentDictionary<int, Image<Gray, byte>> PagesDict = new();

    private static readonly List<TuneType> TuneTypeList = [.. Enum.GetValues(typeof(TuneType)).Cast<TuneType>().Order()];

    private readonly LineType _lineType = LineType.AntiAlias;
    private MCvScalar _scale = new(255);

    private static RepeatType _repType = RepeatType.Double;


    /// <summary>
	/// Creates the entire tune as a list of images, each representing a page.
	/// </summary>
	/// <param name="tune">The full tune to be assembled.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of images for each page.</returns>
    public async Task<List<Image<Gray, byte>>> CreateTune(TuneFull tune)
    {
        _repType = tune.RepeatType;

        var title = CreateTitlePage(tune.Title, tune.TuneType, $"{tune.Key.NoteType} {tune.Key.Keytype}", tune.Composer);

        var listOfPieces = await Task.WhenAll(
            tune.tune.Select(piece => Task.Run(() => AssemblePageSegments(piece, tune.TuneType)))
        );

        if (listOfPieces.Any(t => t == null || t.Count == 0))
        {
            return [];
        }

        int numberOfPages = (int)Math.Ceiling(tune.tune.Count / 2.0);
        int[] fullPageHeights = new int[numberOfPages];
        Array.Fill(fullPageHeights, title.Height);

        List<List<Image<Gray, byte>>> pages = [];
        List<Image<Gray, byte>> innerList = [title];
        var dummyHeader = new Image<Gray, byte>(title.Width, title.Height, White);

        for (int i = 0; i < listOfPieces.Length; i++)
        {
            foreach (var segment in listOfPieces[i])
            {
                innerList.Add(segment);
                int currentPage = i / 2;
                fullPageHeights[currentPage] += segment.Height;
            }

            bool enfOfPage = (i + 1) % 2 == 0;

            if (enfOfPage)
            {
                pages.Add(innerList);
                innerList = [dummyHeader];
            }
        }

        if (innerList.Count > 1)
        {
            pages.Add(innerList);
        }

        var noteNum = GetWidthOfPageInNotes(tune.TuneType);
        var pageWidth = (DefaultNoteSpace) * noteNum;

        var image = new Image<Gray, byte>(pageWidth, fullPageHeights.Max(), White);

        return await CreatePage(image, pages);
    }

    /// <summary>
    /// Assembles the segments of a tune part into a list of images.
    /// </summary>
    /// <param name="part">The part of the tune to be assembled.</param>
    /// <param name="tuneType">The type of the tune.</param>
    /// <returns>A list of images representing the assembled segments of the part.</returns>
    public static List<Image<Gray, byte>> AssemblePageSegments(TunePart part, TuneType tuneType)
    {
        var pieces = new List<Image<Gray, byte>>();

        var newPart = CreatePart(part, tuneType, part.PartNumber);
        pieces.Add(newPart);

        int defaultHeight = 50;
        int pageHeight = newPart.Height;

        int pageWidth = DefaultNoteSpace * GetWidthOfPageInNotes(tuneType);
        var partSplit = new Image<Gray, byte>(pageWidth, defaultHeight, White);

        if (part.Link.line.Count > 0)
        {
            pieces.Add(CreateTuneLink(part.Link, tuneType));
            pieces.Add(partSplit);
            pageHeight += linkHeight;
        }

        if (tuneType == TuneType.Reel)
        {
            pieces.Add(partSplit);
            pageHeight += extraGap + partSplit.Height;
        }

        PartsDict.AddOrUpdate(part.PartNumber, pieces, (key, existing) => pieces);

        return pieces;
    }


    /// <summary>
	/// Creates and assembles the pages of a tune.
	/// </summary>
	/// <param name="image">The image representing the base of the page.</param>
	/// <param name="pages">The list of pages to be assembled.</param>
	/// <returns>A task that represents the asynchronous operation. The task result contains a list of images for each assembled page.</returns>
    public async Task<List<Image<Gray, byte>>> CreatePage(Image<Gray, byte> image, List<List<Image<Gray, byte>>> pages)
    {
        List<Image<Gray, byte>> outPages = new();

        foreach (var page in pages.Select((value, j) => (value, j)))
        {
            await Task.Factory.StartNew(() => AssemblePage(page.value, image, page.j));
        }

        while (PagesDict.Count != pages.Count) { }

        return [.. PagesDict.Values];

    }

    /// <summary>
    /// Assembles a single page from a list of images.
    /// </summary>
    /// <param name="page">The list of images representing the segments of the page.</param>
    /// <param name="image">The base image for the page.</param>
    /// <param name="pageNumber">The number of the page being assembled.</param>
    public void AssemblePage(List<Image<Gray, byte>> page, Image<Gray, byte> image, int pageNumber)
    {
        var pageImage = new Image<Gray, byte>(image.Width, image.Height, White);
        int pageMarker = 0;

        foreach (var piece in page)
        {
            int xPosition = 0;
            int yPosition = pageMarker;

            if (piece.Height == linkHeight)
            {
                xPosition = pageImage.Width - piece.Width - extraGap - 2;
                yPosition -= 70;
            }

            pageImage = SetRoi(pageImage, piece, xPosition, yPosition);
            pageMarker += piece.Height;
        }

        double widthRatio = A4Width / A4Height;
        int a4Height, a4Width, middleMark;

        if (pageImage.Height * widthRatio < pageImage.Width)
        {
            a4Height = (int)(pageImage.Width / widthRatio);
            a4Width = pageImage.Width + 10;
            middleMark = 0;
            pageImage = ShiftOver(pageImage, 25, 0);
        }
        else
        {
            a4Height = pageImage.Height;
            a4Width = (int)(pageImage.Height * widthRatio);
            middleMark = (a4Width - pageImage.Width) / 2;
            pageImage = ShiftOver(pageImage, 10, 0);
        }

        var imageA4 = new Image<Gray, byte>(a4Width, a4Height, White);
        imageA4 = SetRoi(imageA4, pageImage, middleMark, 0);

        PagesDict.AddOrUpdate(pageNumber, imageA4, (key, existingImage) => imageA4);
    }



    #region Title Generation


    /// <summary>
    /// Creates the title page for the tune.
    /// </summary>
    /// <param name="title">The title of the tune.</param>
    /// <param name="tuneType">The type of the tune.</param>
    /// <param name="key">The key of the tune.</param>
    /// <param name="composer">The composer of the tune.</param>
    /// <returns>An image representing the title page.</returns>
    public Image<Gray, byte> CreateTitlePage(string title, TuneType tuneType, string key, string composer)
    {
        int pageHeight = (int)(DefaultNoteHeight * 2) + extraGap;
        int pageWidth = DefaultNoteSpace * (GetWidthOfPageInNotes(tuneType));

        var image = new Image<Gray, byte>(pageWidth, pageHeight);

        var titlePage = CreateTitle(image, title, pageWidth, pageHeight);

        titlePage = CreateTitleSideText(titlePage, tuneType.ToString(), 150);
        titlePage = CreateTitleSideText(titlePage, key, 175);
        titlePage = CreateTitleRightSideText(titlePage, composer, 150);

        CvInvoke.BitwiseNot(titlePage, titlePage);

        return titlePage;
    }

    /// <summary>
    /// Creates the Title text for the title page of the tune.
    /// </summary>
    /// <param name="titlePage">The title page image of the tune.</param>
    /// <param name="title">The title of the tune.</param>
    /// <param name="pageWidth">The page width.</param>
    /// <param name="pageHeight">The page height.</param>
    /// <returns>An image representing the title page containing the title text</returns>
    private Image<Gray, byte> CreateTitle(Image<Gray, byte> titlePage, string title, int pageWidth, int pageHeight)
    {
        int baseline = 1;
        double titleFontScale = title.Length > 9 ? (title.Length > 18 ? 1.5 : 2) : 3;
        int titleFontThickness = title.Length > 9 ? (title.Length > 18 ? 1 : 2) : 3;

        Size titleSize = CvInvoke.GetTextSize(title, DefaultFont, titleFontScale, titleFontThickness, ref baseline);

        double textWidth = ((pageWidth / 2) - (titleSize.Width / 2));
        double textHeight = ((pageHeight / 3) - (titleSize.Height * 0.1));

        int titleStartX = (int)Math.Round(textWidth, 0, MidpointRounding.ToZero);
        int titleStartY = (int)Math.Round(textHeight, 0, MidpointRounding.ToZero);
        int addOn = (title.Length > 9 ? (title.Length > 18 ? 13 : 9) : 0);
        titleStartY += addOn;

        Point point = new(titleStartX, titleStartY);

        CvInvoke.PutText(titlePage, title, point, DefaultFont, titleFontScale, _scale, titleFontThickness, _lineType);

        titlePage = CreateTitleLine(titlePage, titleStartX, titleStartY -= addOn, titleSize.Width, _scale);

        return titlePage;
    }

    /// <summary>
    /// Creates the underline for the title page of the tune.
    /// </summary>
    /// <param name="titlePage">The title page image of the tune.</param>
    /// <param name="titleStartX">The X coord of the start of the line.</param>
    /// <param name="titleStartY">The Y coord of the start of the line.</param>
    /// <param name="width">The page width.</param>
    /// <param name="colour">The line colour.</param>
    /// <returns>The title page with an underline</returns>
    private static Image<Gray, byte> CreateTitleLine(Image<Gray, byte> titlePage, int titleStartX, int titleStartY, int width, MCvScalar colour)
    {
        int lineThickness = 3;

        var startX = titleStartX - DefaultNoteHeight;
        var endX = titleStartX + width + DefaultNoteHeight;
        var lineHeight = titleStartY + 30;

        Point startPoint = new(startX, lineHeight);
        Point endPoint = new(endX, lineHeight);

        CvInvoke.Line(titlePage, startPoint, endPoint, colour, lineThickness);

        return titlePage;
    }

    /// <summary>
    /// Adds tune attributes like the key and the tune type to the left side of the title page
    /// </summary>
    /// <param name="titlePage">The title page image of the tune.</param>
    /// <param name="text">The infirmation being added to the page</param>
    /// <param name="height">The height at which the text is being inserted at</param>
    /// <returns>The title page with added information on the left</returns>
    private Image<Gray, byte> CreateTitleSideText(Image<Gray, byte> titlePage, string text, int height)
    {
        double titleFontScale = 0.65;
        int titleFontThickness = 1;

        Point tuneTypePoint = new(DefaultNoteGap, height + 30);

        CvInvoke.PutText(titlePage, text, tuneTypePoint, DefaultFont, titleFontScale, _scale, titleFontThickness, _lineType);

        return titlePage;
    }

    /// <summary>
    /// Adds tune attributes like the key and the tune type to the left side of the title page
    /// </summary>
    /// <param name="titlePage">The title page image of the tune.</param>
    /// <param name="text">The infirmation being added to the page</param>
    /// <param name="height">The height at which the text is being inserted at</param>
    /// <returns>The title page with added information on the left</returns>
    private Image<Gray, byte> CreateTitleRightSideText(Image<Gray, byte> titlePage, string text, int height)
    {
        double titleFontScale = 0.65;
        int titleFontThickness = 1;

        Point tuneTypePoint = new(titlePage.Cols - 200 - (text.Length * 7), height + 30);
        Point tuneTypePoint2 = new(titlePage.Cols - 200 - (text.Length * 7), height + 55);

        string composer = text.Length > 0 ? "Composer" : "";

        CvInvoke.PutText(titlePage, composer, tuneTypePoint, DefaultFont, titleFontScale, _scale, titleFontThickness, _lineType);
        CvInvoke.PutText(titlePage, text, tuneTypePoint2, DefaultFont, titleFontScale, _scale, titleFontThickness, _lineType);

        return titlePage;
    }

    #endregion

    #region Part Generation

    /// <summary>
    /// Creates a part of the tune as an image.
    /// </summary>
    /// <param name="part">The part of the tune.</param>
    /// <param name="tuneType">The type of the tune.</param>
    /// <param name="partNumber">The number of the part.</param>
    /// <returns>An image representing the part of the tune.</returns>
    public static Image<Gray, byte> CreatePart(TunePart part, TuneType tuneType, int partNumber)
    {
        int extra = part.part.Count > 5 ? DefaultNoteWidth : 0;
        int pageHeight = (int)((DefaultNoteHeight + DefaultNoteWidth) * (part.part.Count + 1) + extra);
        int pageWidth = DefaultNoteSpace * (GetWidthOfPageInNotes(tuneType));

        var image = new Image<Gray, byte>(pageWidth, pageHeight, White);

        var lineSplit = new Image<Gray, byte>(pageWidth, 40, White);

        for (int i = 0; i < part.part.Count; i++)
        {
            var imageLine = CreateLine(part.part[i], i, pageWidth);
            image = SetRoi(image, imageLine, 0, (i * 160) + 10);
            image = SetRoi(image, lineSplit, 0, (120 + (i * 160)) + 10);
        }

        if (NoteImage.GetImage($"part{partNumber}") is { } noteImage)
        {
            image = SetRoi(image, noteImage, 10, 5);
        }

        if (NoteImage.GetRepImage(_repType) is { } partRepeat)
        {
            image = SetRoi(image, partRepeat, 37, DefaultNoteHeight);
        }

        if (NoteImage.GetImage("x") is { } partRepeatX)
        {
            image = SetRoi(image, partRepeatX, 19, DefaultNoteHeight + 7);
        }

        return image;
    }

    public static Image<Gray, byte> CreateLine(TuneLine line, int lineCount, int pageWidth, int pageHeight = 0, bool ifLink = false)
    {
        int lineHeight = DefaultNoteHeight + 40 + pageHeight;
        var image = new Image<Gray, byte>(pageWidth, lineHeight, White);
        var space = NoteImage.GetImage("space");

        for (int i = 0; i < line.line.Count; i++)
        {
            var bar = line.line[i].bar;
            var imageNote = BarCache.GetOrAdd(bar, _ => CreateBar(line.line[i]));

            imageNote ??= CreateBar(line.line[i]);

            BarCache.TryAdd(line.line[i].bar, imageNote);

            int barWidth = (DefaultNoteSpace * line.line[i].CurrentLength) + 55;
            int xPosition = i * barWidth + (ifLink ? 40 : DefaultNoteHeight);

            image = SetRoi(image, imageNote, xPosition);

            if (!ifLink)
            {
                int spacePosition = (i + 1) * barWidth + 25;
                image = SetRoi(image, space, spacePosition);
            }
        }

        return image;
    }

    /// <summary>
    /// Creates a bar of the tune as an image.
    /// </summary>
    /// <param name="bar">The bar of the tune.</param>
    /// <returns>An image representing the bar of the tune.</returns>
    public static Image<Gray, byte> CreateBar(TuneBar bar)
    {
        int barWidth = DefaultNoteSpace * bar.CurrentLength;
        int barHeight = DefaultNoteHeight + 40;
        var image = new Image<Gray, byte>(barWidth, barHeight, White);

        var tripCount = 0;
        int shift = 0;

        for (int i = 0; i < bar.bar.Count; i++)
        {
            Image<Gray, byte> imageNote;

            switch (bar.bar[i])
            {
                case Duplet duplet:
                    imageNote = CreateDuplet(duplet);
                    break;

                case Triplet triplet:
                    imageNote = CreateTriplet(triplet);
                    tripCount++;
                    break;

                default:
                    var singlet = new Singlet(bar.bar[i].Note);
                    imageNote = CreateNote(singlet);
                    break;
            }

            int xPosition = (i + shift) * (DefaultNoteSpace);
            image = SetRoi(image, imageNote, xPosition);
            shift = tripCount;
        }

        return image;
    }

    /// <summary>
    /// Constructs an Image of a note using the specified attributes in the provided "Note" object
    /// </summary>
    /// <param name="image">A Blank Image</param>
    /// <param name="noteImage">The note to be returned</param>
    /// <param name="note">The note to be converted to an Image</param>
    /// <param name="highShiftSide">The amount of pixels to shift the sharp/flat/low note to the side</param>
    /// <param name="highShiftDown">The amount of pixels to shift the sharp/flat/ low note down</param>
    /// <returns>An image representing the part of the tune.</returns>
    public static Image<Gray, byte> ArrangeNote(Image<Gray, byte> image, Image<Gray, byte> noteImage, Note note, int highShiftSide = 0, int highShiftDown = 0)
    {
        if (note.ShortLongNote)
        {
            noteImage = AddShortLong(noteImage);
        }

        switch (note.OctaveType)
        {
            case OctaveType.High:
                noteImage = note.AccidentalType switch
                {
                    AccidentalType.Sharp => AddHighAndSharp(noteImage),
                    AccidentalType.Flat => AddHighAndFlat(noteImage),
                    _ => AddHigh(noteImage, highShiftSide, highShiftDown)
                };
                break;

            case OctaveType.Low:
                noteImage = AddLow(noteImage, highShiftSide);
                noteImage = note.AccidentalType switch
                {
                    AccidentalType.Sharp => AddSharp(noteImage),
                    AccidentalType.Flat => AddFlat(noteImage, 2),
                    AccidentalType.Natural => AddNatural(noteImage, highShiftSide),
                    _ => noteImage
                };
                break;

            default:
                noteImage = note.AccidentalType switch
                {
                    AccidentalType.Sharp => AddSharp(noteImage),
                    AccidentalType.Flat => AddFlat(noteImage, highShiftSide),
                    AccidentalType.Natural => AddNatural(noteImage, highShiftSide),
                    _ => noteImage
                };
                break;
        }

        return noteImage;
    }

    /// <summary>
    /// Creates a note of the tune as an image.
    /// </summary>
    /// <param name="note">A note from the tune.</param>
    /// <returns>An image representing the note of the tune.</returns>
    public static Image<Gray, byte> CreateNote(Singlet note)
    {
        Image<Gray, byte> noteImage = NoteImage.GetImage(key: note.Note.NoteType.ToString());
        var image = new Image<Gray, byte>(DefaultNoteSpace, DefaultNoteHeight + 40, White);

        noteImage = SetRoi(image, noteImage, 4, 18);

        if (note.Note.NoteType == NoteType.r || note.Note.NoteType == NoteType.l)
        {
            noteImage = ShiftBack(noteImage, 0, 10);
        }

        noteImage = ArrangeNote(image, noteImage, note.Note, 10, 0);

        return noteImage;
    }

    /// <summary>
    /// Creates a duplet as an image.
    /// </summary>
    /// <param name="bar">A pair of notes from the tune.</param>
    /// <returns>An image representing a duplet.</returns>
    /// <remarks>
    /// A "Duplet" here means two notes that are played quickly to take up the same
    /// space of time as a normal note
    /// </remarks>
    public static Image<Gray, byte> CreateDuplet(Duplet bar)
    {
        List<Note> notes = [bar.FirstNote, bar.SecondNote];

        var foo2 = new NoteImage();

        var fullImage = new Image<Gray, byte>((DefaultNoteWidth) * 2, DefaultNoteHeight + DefaultNoteWidth, White);
        var resize = new Image<Gray, byte>((int)Math.Round(((DefaultNoteWidth) * 2) * 0.633333), (int)Math.Round((DefaultNoteHeight + DefaultNoteWidth) * 0.633333), White);

        foreach (var note in notes.Select((value, i) => (value, i)))
        {
            Image<Gray, byte> noteImage = NoteImage.GetImage(note.value.NoteType.ToString());

            var image = new Image<Gray, byte>(noteImage.Width, DefaultNoteHeight + 40, White);

            noteImage = SetRoi(image, noteImage, 0, 18);
            int space = note.value.OctaveType == OctaveType.Low ? 0 : 5;
            noteImage = ArrangeNote(image, noteImage, note.value, space, 0);
            fullImage = SetRoi(fullImage, image, (note.i * (DefaultNoteWidth)), 0);
        }

        CvInvoke.Resize(fullImage, resize, new Size(), 0.633333, 0.633333);

        var bigImage = new Image<Gray, byte>(DefaultNoteSpace, DefaultNoteHeight + 40, White);
        var dupImage = NoteImage.GetImage("duplet");

        bigImage = ShiftOver(
            SetRoi(SetRoi(bigImage, dupImage), resize, 0, 25)
            , 0, 10);
        return bigImage;
    }


    /// <summary>
    /// Creates a triplet as an image.
    /// </summary>
    /// <param name="bar">Three notes from the tune.</param>
    /// <returns>An image representing a triplet.</returns>
    /// <remarks>
    /// A "Triplet" here means three notes that are played quickly to take up the same
    /// space of time as two normal notes
    /// </remarks>
    public static Image<Gray, byte> CreateTriplet(Triplet bar)
    {
        var notes = new[] { bar.FirstNote, bar.SecondNote, bar.ThirdNote };

        var fullImageWidth = (DefaultNoteWidth + 4) * 3;
        var fullImageHeight = DefaultNoteHeight + 40;
        var resizeScale = 0.79166667;

        var fullImage = new Image<Gray, byte>(fullImageWidth, fullImageHeight, White);
        var resizeImage = new Image<Gray, byte>((int)(fullImageWidth * resizeScale), (int)(fullImageHeight * resizeScale), White);

        for (int i = 0; i < notes.Length; i++)
        {
            var note = notes[i];
            if (note == null)
            {
                continue;
            }

            var noteImage = NoteImage.GetImage(note.NoteType.ToString());

            if (noteImage != null)
            {
                var image = new Image<Gray, byte>(DefaultNoteWidth + 4, DefaultNoteHeight + 40, White);
                image = SetRoi(image, noteImage, 4, 18);
                image = ArrangeNote(image, image, note, 4, 2);
                fullImage = SetRoi(fullImage, image, i * (DefaultNoteWidth + 4), 0);
            }
        }

        CvInvoke.Resize(fullImage, resizeImage, new Size(), resizeScale, resizeScale);

        var tripImage = NoteImage.GetImage("triplet");
        var bigImage = new Image<Gray, byte>(DefaultNoteSpace * 2, fullImageHeight, White);
        bigImage = SetRoi(bigImage, tripImage);
        bigImage = SetRoi(bigImage, resizeImage, 0, 22);

        return bigImage;
    }

    /// <summary>
    /// Creates a link for a part to transition to another part as an image.
    /// </summary>
    /// <param name="line">The line from the tune to be converted to a link image</param>
    /// <param name="tuneType">TThe type of tune</param>
    /// <returns>An image representing the link for a part.</returns>
    public static Image<Gray, byte> CreateTuneLink(TuneLine line, TuneType tuneType)
    {
        int middleCount = line.MaxLength * (line.line[0].MaxLength + 1);
        var linkStart = NoteImage.GetImage("linkStart");
        int linkWidth = linkStart.Width;
        int linkHeight = linkStart.Height;

        var image = new Image<Gray, byte>(linkWidth * middleCount, linkHeight, White);

        image = SetRoi(image, NoteImage.GetImage("linkStart"), 0, 0);

        for (int i = 1; i < middleCount - 1; i++)
        {
            int xPosition = i * linkWidth;
            image = SetRoi(image, NoteImage.GetImage("linkMiddle"), xPosition, 0);
        }

        image = SetRoi(image, NoteImage.GetImage("linkEnd"), (middleCount - 1) * linkWidth, 0);

        var lineImage = CreateLine(line, 0, image.Width, 21, true);
        lineImage = ShiftOver(lineImage, 0, 20);

        CvInvoke.BitwiseAnd(image, lineImage, image);

        return image;
    }

    /// <summary>
    /// Inserts a smaller image into a larger image within a specified location within the larger image
    /// </summary>
    /// <param name="bigImage">A larger image being changed</param>
    /// <param name="smallImage">The smaller image being inserted into the ROI</param>
    /// <param name="shiftWidth">The amount that the ROI is shifted over from the left</param>
    /// <param name="shiftHeight">The amount that the ROI is shifted over from the top</param>
    /// <returns>The larger Image with the smaller inserted into it</returns>
    private static Image<Gray, byte> SetRoi(Image<Gray, byte> bigImage, Image<Gray, byte> smallImage, int shiftWidth = 0, int shiftHeight = 0)
    {
        shiftWidth = shiftWidth + smallImage.Width > bigImage.Width? bigImage.Width - smallImage.Width: shiftWidth;
        shiftHeight = shiftHeight + smallImage.Height > bigImage.Height ? bigImage.Height - smallImage.Height : shiftHeight;

        if (shiftWidth < 0 || shiftHeight < 0 ||
            shiftWidth + smallImage.Width > bigImage.Width ||
            shiftHeight + smallImage.Height > bigImage.Height)
        {
            throw new ArgumentException("The small image does not fit in the specified region.");
        }

        Rectangle roi = new Rectangle(shiftWidth, shiftHeight, smallImage.Width, smallImage.Height);

        bigImage.ROI = roi;
        smallImage.CopyTo(bigImage);
        bigImage.ROI = Rectangle.Empty;

        return bigImage;
    }

    private static Image<Gray, byte> ShiftOver(Image<Gray, byte> image, int shiftWidth = 0, int shiftHeight = 0)
        => ShiftImage(image, true, shiftWidth, shiftHeight);
    private static Image<Gray, byte> ShiftBack(Image<Gray, byte> image, int shiftWidth = 0, int shiftHeight = 0)
        => ShiftImage(image, false, -shiftWidth, -shiftHeight);
    private static Image<Gray, byte> ShiftImage(Image<Gray, byte> image, bool over, int shiftWidth = 0, int shiftHeight = 0)
    {
        var shiftedImage = new Image<Gray, byte>(image.Size);

        var transformationMatrix = new Matrix<float>(2, 3);

        transformationMatrix[0, 0] = 1;
        transformationMatrix[0, 1] = 0;
        transformationMatrix[0, 2] = shiftWidth;

        transformationMatrix[1, 0] = 0;
        transformationMatrix[1, 1] = 1;
        transformationMatrix[1, 2] = shiftHeight;

        CvInvoke.WarpAffine(image, shiftedImage, transformationMatrix, image.Size, Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(255));

        return shiftedImage;
    }


    private static int GetWidthOfPageInNotes(TuneType tuneType)
    {
        return ((int)tuneType / ((TuneTypeList.IndexOf(tuneType) + 1) * 10));
    }
    private static Image<Gray, byte> AddSharp(Image<Gray, byte> bigImage)
        => AddAboveNote(bigImage, NoteImage.GetImage("sharp"));
    private static Image<Gray, byte> AddFlat(Image<Gray, byte> bigImage, int small = 0)
        => AddAboveNote(bigImage, NoteImage.GetImage("flat"), 10 - small);
    private static Image<Gray, byte> AddNatural(Image<Gray, byte> bigImage, int small = 0)
        => AddAboveNote(bigImage, NoteImage.GetImage("natural"), 10 - small);
    private static Image<Gray, byte> AddHigh(Image<Gray, byte> bigImage, int shiftSide = 0, int shiftDown = 0)
        => AddAboveNote(bigImage, NoteImage.GetImage("high"), shiftSide, shiftDown);

    private static Image<Gray, byte> AddHighAndSharp(Image<Gray, byte> bigImage)
    {
        var high = NoteImage.GetImage("high");
        int width = high is null ? 0 : high.Width;
        return AddAboveNote(AddAboveNote(bigImage, high), NoteImage.GetImage("sharp"), width, 1);
    }

    private static Image<Gray, byte> AddHighAndFlat(Image<Gray, byte> bigImage)
    {
        var high = NoteImage.GetImage("high");
        int width = high is null ? 0 : high.Width;
        return AddAboveNote(AddAboveNote(bigImage, high), NoteImage.GetImage("flat"), width, 1);
    }

    private static Image<Gray, byte> AddAboveNote(Image<Gray, byte> bigImage, Image<Gray, byte>? symbolImage, int shiftSide = 0, int shiftDown = 0)
    {
        if (symbolImage is null)
        {
            return bigImage;
        }

        if (shiftSide < 0 || shiftDown < 0 ||
            shiftSide + symbolImage.Width > bigImage.Width ||
            shiftDown + symbolImage.Height > bigImage.Height)
        {
            throw new ArgumentException("The symbol image does not fit within the specified region.");
        }

        Image<Gray, byte> newImage = new (bigImage.Width, bigImage.Height, new Gray(255)); 

        Rectangle roi = new (bigImage.Width - symbolImage.Width - shiftSide, shiftDown, symbolImage.Width, symbolImage.Height);

        newImage.ROI = roi;
        symbolImage.CopyTo(newImage);
        newImage.ROI = Rectangle.Empty;

        CvInvoke.BitwiseAnd(bigImage, newImage, bigImage);

        return bigImage;
    }


    private static Image<Gray, byte> AddLow(Image<Gray, byte> bigImage, int sideShift) 
        => AddBelowNote(bigImage, NoteImage.GetImage("low"), sideShift, DefaultNoteHeight + 18);
    private static Image<Gray, byte> AddShortLong(Image<Gray, byte> bigImage)
    {
        var shortLong = NoteImage.GetImage("__");
        int width = shortLong is null ? 0 : shortLong.Width;
        return AddBelowNote(bigImage, shortLong, (DefaultNoteWidth + 15 - (width + 1)), DefaultNoteSpace + 16);
    }
    
    private static Image<Gray, byte> AddBelowNote(Image<Gray, byte> bigImage, Image<Gray, byte>? symbolImage, int shiftSide = 0, int shiftDown = 0)
    {
        if (symbolImage is null)
        {
            return bigImage;
        }
        if (shiftSide < 0 || shiftDown < 0 ||
            shiftSide + symbolImage.Width > bigImage.Width ||
            shiftDown + symbolImage.Height > bigImage.Height)
        {
            throw new ArgumentException("The symbol image does not fit within the specified region.");
        }

        Rectangle roi = new (shiftSide, shiftDown, symbolImage.Width, symbolImage.Height);

        using (var newImage = new Image<Gray, byte>(bigImage.Width, bigImage.Height, new Gray(255)))
        {
            newImage.ROI = roi;
            symbolImage.CopyTo(newImage);
            newImage.ROI = Rectangle.Empty;

            CvInvoke.BitwiseAnd(bigImage, newImage, bigImage);
        }

        return bigImage;
    }

    #endregion

    private static void DisplayImage(List<Image<Gray, byte>> assembledPages)
    {
        foreach (var assembledPage in assembledPages)
        {
            Image<Gray, byte> resizedPage = new(assembledPage.Width, assembledPage.Height);

            CvInvoke.Resize(assembledPage, resizedPage, new Size(), 0.5, 0.5);

            CvInvoke.Imshow("s", resizedPage);
            CvInvoke.WaitKey();
        }
    }
}
