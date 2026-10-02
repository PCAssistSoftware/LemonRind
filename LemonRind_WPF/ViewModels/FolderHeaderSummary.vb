Imports CommunityToolkit.Mvvm.Input

Namespace ViewModels

    ''' <summary>
    ''' One collapsible folder header row in the sidebar's flat SidebarItems
    ''' list (see MainViewModel.RebuildSidebarItems). A plain data object
    ''' with its own ToggleExpandedCommand, not a shared command on
    ''' MainViewModel with a CommandParameter - the row's DataTemplate just
    ''' binds directly to it, ordinary MVVM commanding, not the ContextMenu-
    ''' style PlacementTarget workaround the session row's right-click menu
    ''' needs (that workaround exists specifically because a ContextMenu
    ''' opens in its own visual tree - a plain row Button doesn't have that
    ''' problem).
    ''' </summary>
    Public Class FolderHeaderSummary
        Public Property FolderId As String
        Public Property Name As String
        Public Property IsCollapsed As Boolean

        ''' <summary>False only for the synthetic "Unfiled" bucket - it has no real Folders row to persist a collapsed state against, so it always stays expanded and isn't clickable.</summary>
        Public Property IsCollapsible As Boolean = True

        Public Property ToggleExpandedCommand As IRelayCommand
    End Class

End Namespace
