using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlightPlanWindowPlugin.Views;

/// <summary>
/// Behaviours for the text boxes in the Flight Plan Window.
/// </summary>
public static class TextBoxBehaviours
{
    /// <summary>
    /// A regular expression for one character. The text box rejects the characters that do not match.
    /// </summary>
    public static readonly DependencyProperty AllowedCharactersProperty = DependencyProperty.RegisterAttached(
        "AllowedCharacters",
        typeof(string),
        typeof(TextBoxBehaviours),
        new PropertyMetadata(null, OnAllowedCharactersChanged));

    public static string? GetAllowedCharacters(TextBox textBox) => (string?)textBox.GetValue(AllowedCharactersProperty);

    public static void SetAllowedCharacters(TextBox textBox, string? value) => textBox.SetValue(AllowedCharactersProperty, value);

    static void OnAllowedCharactersChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBox textBox)
            return;

        textBox.PreviewTextInput -= RejectCharacters;
        DataObject.RemovePastingHandler(textBox, RejectPaste);

        if (args.NewValue is null)
            return;

        textBox.PreviewTextInput += RejectCharacters;
        DataObject.AddPastingHandler(textBox, RejectPaste);
    }

    static void RejectCharacters(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IsAllowed((TextBox)sender, e.Text);
    }

    static void RejectPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !IsAllowed((TextBox)sender, text))
            e.CancelCommand();
    }

    static bool IsAllowed(TextBox textBox, string text)
    {
        var pattern = GetAllowedCharacters(textBox);
        return pattern is null || text.All(character => Regex.IsMatch(character.ToString(), $"^{pattern}$"));
    }
}
