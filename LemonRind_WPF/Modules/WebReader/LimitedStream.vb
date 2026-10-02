Imports System.IO
Imports System.Threading

Namespace Modules.WebReader

    ''' <summary>
    ''' Wraps a stream and throws once more than maxBytes have been read -
    ''' caps how much a single fetched page can consume, so a malicious or
    ''' just enormous response (a multi-gigabyte file served at a normal-
    ''' looking URL) can't exhaust memory before the size is known. Only the
    ''' read path used by SsrfSafeHttpFetcher (ReadAsync) is implemented;
    ''' this isn't a general-purpose Stream.
    ''' </summary>
    Friend Class LimitedStream
        Inherits Stream

        Private ReadOnly _inner As Stream
        Private ReadOnly _maxBytes As Long
        Private _bytesRead As Long = 0

        Public Sub New(inner As Stream, maxBytes As Long)
            _inner = inner
            _maxBytes = maxBytes
        End Sub

        ' VB's Async modifier can't return ValueTask(Of T) directly - this
        ' non-Async wrapper adapts an Async Task(Of Integer) helper's result
        ' into the ValueTask Stream requires.
        Public Overrides Function ReadAsync(buffer As Memory(Of Byte), Optional cancellationToken As CancellationToken = Nothing) As ValueTask(Of Integer)
            Return New ValueTask(Of Integer)(ReadAsyncCore(buffer, cancellationToken))
        End Function

        Private Async Function ReadAsyncCore(buffer As Memory(Of Byte), cancellationToken As CancellationToken) As Task(Of Integer)
            Dim read = Await _inner.ReadAsync(buffer, cancellationToken)
            _bytesRead += read
            If _bytesRead > _maxBytes Then
                Throw New InvalidOperationException($"Response exceeded the {_maxBytes:N0}-byte limit for a fetched page.")
            End If
            Return read
        End Function

        Public Overrides Function Read(buffer() As Byte, offset As Integer, count As Integer) As Integer
            ' Named bytesReadNow rather than "read" - VB is case-insensitive,
            ' so a local named "read" would collide with this method's own
            ' name "Read".
            Dim bytesReadNow = _inner.Read(buffer, offset, count)
            _bytesRead += bytesReadNow
            If _bytesRead > _maxBytes Then
                Throw New InvalidOperationException($"Response exceeded the {_maxBytes:N0}-byte limit for a fetched page.")
            End If
            Return bytesReadNow
        End Function

        Public Overrides ReadOnly Property CanRead As Boolean
            Get
                Return True
            End Get
        End Property

        Public Overrides ReadOnly Property CanSeek As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property CanWrite As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Length As Long
            Get
                Throw New NotSupportedException()
            End Get
        End Property

        Public Overrides Property Position As Long
            Get
                Throw New NotSupportedException()
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property

        Public Overrides Sub Flush()
        End Sub

        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function

        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub

        Public Overrides Sub Write(buffer() As Byte, offset As Integer, count As Integer)
            Throw New NotSupportedException()
        End Sub

    End Class

End Namespace
