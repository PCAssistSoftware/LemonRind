Namespace Services

    ''' <summary>Which model a scheduled job should use, and anything the person should be told about it. Model Nothing means "leave the default as it is".</summary>
    Public NotInheritable Class JobModelChoice

        Public ReadOnly Property Model As String
        Public ReadOnly Property Note As String
        Public ReadOnly Property Problem As String

        Public Sub New(model As String, note As String, problem As String)
            Me.Model = model
            Me.Note = note
            Me.Problem = problem
        End Sub

    End Class

    ''' <summary>
    ''' Checks the default chat model (Settings > Lemonade) against what Lemonade really has. A model's name can change when
    ''' Lemonade is updated; the setting then names a model that is gone, and everything that relies on the default fails
    ''' with "model not found". The Settings page warns about it, and a scheduled job with no model of its own falls back to
    ''' the chat model Lemonade has loaded instead of failing.
    ''' </summary>
    Public NotInheritable Class DefaultModelCheck

        Private Sub New()
        End Sub

        Private Shared Function IsChat(model As LemonadeModelInfo) As Boolean
            Return model.Labels IsNot Nothing AndAlso model.Labels.Any(Function(label) String.Equals(label, "chat", StringComparison.OrdinalIgnoreCase))
        End Function

        Public Shared Function IsListed(model As String, models As IReadOnlyList(Of LemonadeModelInfo)) As Boolean
            Return models.Any(Function(m) String.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>A sentence for Settings when the default chat model is not one Lemonade has, else "" (also "" when the list could not be fetched).</summary>
        Public Shared Function WarningFor(model As String, models As IReadOnlyList(Of LemonadeModelInfo)) As String
            If String.IsNullOrWhiteSpace(model) OrElse models.Count = 0 OrElse IsListed(model.Trim(), models) Then Return ""

            Dim examples = models.Where(AddressOf IsChat).Select(Function(m) m.Id).Take(5).ToList()
            Dim hint = If(examples.Count > 0, $" Chat models on Lemonade include: {String.Join(", ", examples)}.", "")
            Return $"'{model.Trim()}' is not one of the models Lemonade has now. A scheduled job that uses the default model will use whichever chat model is loaded instead, and fail if none is.{hint}"
        End Function

        ''' <summary>
        ''' The model a scheduled job should use. A model chosen for the job is always used as it is. With none chosen the
        ''' default chat model is used when Lemonade has it; if not, the chat model that is loaded takes its place (with a note
        ''' saying so), and with no chat model loaded the job is told why it cannot run.
        ''' </summary>
        Public Shared Function ChooseForJob(jobModel As String, defaultModel As String, models As IReadOnlyList(Of LemonadeModelInfo), health As LemonadeHealthInfo) As JobModelChoice
            If Not String.IsNullOrWhiteSpace(jobModel) Then Return New JobModelChoice(jobModel, "", "")
            If models.Count = 0 Then Return New JobModelChoice(Nothing, "", "") ' could not ask Lemonade: carry on as before
            If Not String.IsNullOrWhiteSpace(defaultModel) AndAlso IsListed(defaultModel, models) Then Return New JobModelChoice(Nothing, "", "")

            Dim names As New List(Of String)
            If health IsNot Nothing Then
                If health.AllModelsLoaded IsNot Nothing Then names.AddRange(health.AllModelsLoaded.Select(Function(m) m.ModelName))
                names.Add(health.ModelLoaded)
            End If
            Dim loaded = names.Where(Function(name) Not String.IsNullOrWhiteSpace(name)).
                Select(Function(name) models.FirstOrDefault(Function(m) String.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase))).
                FirstOrDefault(Function(m) m IsNot Nothing AndAlso IsChat(m))

            Dim shown = If(String.IsNullOrWhiteSpace(defaultModel), "(none set)", defaultModel)
            If loaded IsNot Nothing Then
                Return New JobModelChoice(loaded.Id,
                    $"Note: the default chat model '{shown}' is not on Lemonade now, so this run used '{loaded.Id}', the chat model that was loaded. Update the default in Settings > Lemonade.", "")
            End If
            Return New JobModelChoice(Nothing, "",
                $"The default chat model '{shown}' is not on Lemonade now, and no chat model is loaded. Set the default in Settings > Lemonade, or choose a model for this job.")
        End Function

    End Class

End Namespace
