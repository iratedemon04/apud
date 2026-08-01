using Marc.Core;

namespace Apud.Tests;

/// <summary>
/// The one function every heading comparison funnels through. Cataloguing software
/// rots at diacritics and casing, so this corpus is deliberately mean — Spanish
/// names, accents, ñ, inverted-comma punctuation, the retained first comma.
/// </summary>
public class HeadingNormalizationTests
{
    [Theory]
    [InlineData("Física", "fisica")]
    [InlineData("FÍSICA", "fisica")]
    [InlineData("Muñoz", "munoz")]
    [InlineData("MUÑOZ", "munoz")]
    [InlineData("Peña", "pena")]
    [InlineData("Água", "agua")]
    [InlineData("Öztürk", "ozturk")]
    public void Strips_diacritics_and_casefolds(string input, string expected) =>
        Assert.Equal(expected, HeadingNormalization.Normalize(input));

    [Fact]
    public void Keeps_the_first_comma_but_no_others()
    {
        // The inverted-name comma is meaningful and kept; a second comma is not.
        Assert.Equal("preciado, amado casimiro", HeadingNormalization.Normalize("Preciado, Amado, Casimiro"));
    }

    [Fact]
    public void Space_around_the_kept_comma_is_normalized_away()
    {
        Assert.Equal(
            HeadingNormalization.Normalize("Preciado, Amado"),
            HeadingNormalization.Normalize("Preciado ,  Amado"));
    }

    [Theory]
    [InlineData("¿Quién soy?", "quien soy")]
    [InlineData("¡Hola!", "hola")]
    [InlineData("Física: investigación", "fisica investigacion")]
    [InlineData("Smith-Jones", "smith jones")]
    [InlineData("México/Argentina", "mexico argentina")]
    public void Punctuation_becomes_a_separator_never_a_join(string input, string expected) =>
        Assert.Equal(expected, HeadingNormalization.Normalize(input));

    [Fact]
    public void Collapses_runs_of_whitespace_and_trims()
    {
        Assert.Equal("fisica nuclear investigacion",
            HeadingNormalization.Normalize("  Física   nuclear\tInvestigación  "));
    }

    [Fact]
    public void Accent_folding_makes_accented_and_plain_forms_compare_equal()
    {
        Assert.Equal(
            HeadingNormalization.Normalize("Física nuclear"),
            HeadingNormalization.Normalize("Fisica nuclear"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("¿?¡!")]
    public void Empty_and_punctuation_only_input_normalize_to_empty(string input) =>
        Assert.Equal("", HeadingNormalization.Normalize(input));
}
