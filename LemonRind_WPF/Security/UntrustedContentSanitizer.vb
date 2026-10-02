Imports System.Text

Namespace Security

    ''' <summary>
    ''' The Web reader and Web search modules already warn the model in
    ''' plain text not to treat fetched content as instructions (see
    ''' WebReaderModule.ReadPageAsync/WebSearchModule.SearchAsync), but that
    ''' warning is just more text the model reads - it does nothing to stop
    ''' a page hiding an instruction inside characters that render as
    ''' invisible, or inside letters that *look* like the warning's own
    ''' alphabet but aren't. This module closes that specific gap: it
    ''' strips the actual bytes a page could use for those two tricks
    ''' before the content ever reaches the model, as a second, independent
    ''' layer alongside the existing plain-text warning - same "protection
    ''' alongside, not after" principle this app already applies to *where*
    ''' a request can go (SsrfSafeHttpFetcher), now applied to *what comes
    ''' back*.
    ''' </summary>
    Public Module UntrustedContentSanitizer

        ''' <summary>
        ''' Zero-width and invisible Unicode characters real-world prompt-
        ''' injection attempts have used to hide instructions inside text
        ''' that renders as blank space - none of these have any legitimate
        ''' reason to appear in a web page's readable text.
        ''' </summary>
        Private ReadOnly ZeroWidthChars As New HashSet(Of Char) From {
            ChrW(&H200B), ChrW(&H200C), ChrW(&H200D), ChrW(&H200E), ChrW(&H200F),
            ChrW(&H202A), ChrW(&H202B), ChrW(&H202C), ChrW(&H202D), ChrW(&H202E),
            ChrW(&H2060), ChrW(&H2061), ChrW(&H2062), ChrW(&H2063), ChrW(&H2064),
            ChrW(&HFEFF), ChrW(&HAD)
        }

        ''' <summary>
        ''' A practical (not exhaustive) set of Greek/Cyrillic characters that
        ''' render visually identical to common Latin letters - the classic
        ''' homoglyph trick for smuggling text past a "don't follow
        ''' instructions" warning by spelling it with lookalike letters
        ''' instead of the ones the warning itself is written in.
        ''' </summary>
        Private ReadOnly HomoglyphMap As New Dictionary(Of Char, Char) From {
            {ChrW(&H0391), "A"c}, {ChrW(&H0392), "B"c}, {ChrW(&H0395), "E"c}, {ChrW(&H0396), "Z"c},
            {ChrW(&H0397), "H"c}, {ChrW(&H0399), "I"c}, {ChrW(&H039A), "K"c}, {ChrW(&H039C), "M"c},
            {ChrW(&H039D), "N"c}, {ChrW(&H039F), "O"c}, {ChrW(&H03A1), "P"c}, {ChrW(&H03A4), "T"c},
            {ChrW(&H03A5), "Y"c}, {ChrW(&H03A7), "X"c},
            {ChrW(&H0410), "A"c}, {ChrW(&H0412), "B"c}, {ChrW(&H0415), "E"c}, {ChrW(&H041A), "K"c},
            {ChrW(&H041C), "M"c}, {ChrW(&H041D), "H"c}, {ChrW(&H041E), "O"c}, {ChrW(&H0420), "P"c},
            {ChrW(&H0421), "C"c}, {ChrW(&H0422), "T"c}, {ChrW(&H0425), "X"c},
            {ChrW(&H0430), "a"c}, {ChrW(&H0435), "e"c}, {ChrW(&H043E), "o"c}, {ChrW(&H0440), "p"c},
            {ChrW(&H0441), "c"c}, {ChrW(&H0445), "x"c}, {ChrW(&H0443), "y"c}
        }

        ''' <summary>
        ''' Strips invisible characters and normalizes homoglyphs in text
        ''' fetched from an untrusted external source (a web page, a search
        ''' result snippet) before it's handed to the model. Safe to call on
        ''' any plain text - a page with none of these tricks in it comes
        ''' back unchanged.
        ''' </summary>
        Public Function Sanitize(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text

            Dim builder As New StringBuilder(text.Length)
            For Each c As Char In text
                If ZeroWidthChars.Contains(c) Then Continue For

                Dim replacement As Char
                If HomoglyphMap.TryGetValue(c, replacement) Then
                    builder.Append(replacement)
                Else
                    builder.Append(c)
                End If
            Next

            Return builder.ToString()
        End Function

    End Module

End Namespace
