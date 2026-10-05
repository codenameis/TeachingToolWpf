using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class MatchingNumericInputTests
{
    [Theory]
    [InlineData(PatternMatchingMethod.OpenCvTemplate)]
    [InlineData(PatternMatchingMethod.Shape)]
    [InlineData(PatternMatchingMethod.GeometricShape)]
    public void TypingFractionPreservesDecimalPointAndTrailingZero(PatternMatchingMethod method)
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            model.Method = method;
            var field = Bind(model, nameof(model.MinimumScore));
            TypeCharacters(field, "0.720");
            Assert.Equal(.72, model.MinimumScore);
            Assert.False(Validation.GetHasError(field));
        });
    }

    [Theory]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumAngle), "-0.5", -.5)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumAcceptedAngle), "-0.5", -.5)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumAcceptedAngle), "10.50", 10.5)]
    [InlineData(nameof(ShapeMatchingViewModel.ScaleStep), "0.050", .05)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumGradient), "8.50", 8.5)]
    [InlineData(nameof(ShapeMatchingViewModel.MinimumFeatureContrastRatio), "0.250", .25)]
    [InlineData(nameof(ShapeMatchingViewModel.MaximumMatches), "256", 256)]
    [InlineData(nameof(ShapeMatchingViewModel.SearchTimeoutSeconds), "0.05", .05)]
    public void OtherNumericTypesAndSignedFractionsKeepTheirEditText(string property, string text, double expected)
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            var field = Bind(model, property);
            TypeCharacters(field, text);
            Assert.Equal(expected, System.Convert.ToDouble(typeof(ShapeMatchingViewModel).GetProperty(property)!.GetValue(model)));
            Assert.False(Validation.GetHasError(field));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("0..72")]
    [InlineData("invalid")]
    public void MalformedDraftReportsBindingErrorWithoutReplacingTheTypedValue(string text)
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            var field = Bind(model, nameof(model.MinimumScore));
            var original = model.MinimumScore;
            field.SetCurrentValue(TextBox.TextProperty, text);
            PumpBindings();
            Assert.True(Validation.GetHasError(field));
            Assert.Equal(text, field.Text);
            Assert.Equal(original, model.MinimumScore);
            TypeCharacters(field, ".80");
            Assert.Equal(.8, model.MinimumScore);
            Assert.False(Validation.GetHasError(field));
        });
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1.1")]
    public void ParsedOutOfRangeScoreStillBlocksExecution(string text)
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            var field = Bind(model, nameof(model.MinimumScore));
            field.SetCurrentValue(TextBox.TextProperty, text);
            PumpBindings();
            Assert.NotEmpty(model.OptionsError);
            Assert.False(model.Train.CanExecute(null));
            Assert.False(model.Search.CanExecute(null));
            TypeCharacters(field, "0.72");
            Assert.Empty(model.OptionsError);
        });
    }

    [Fact]
    public void DifferentFieldsAndModeChangesUseTheirOwnTypedSettings()
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            var score = Bind(model, nameof(model.MinimumScore));
            var support = Bind(model, nameof(model.MinimumSupport));
            TypeCharacters(score, "0.820");
            TypeCharacters(support, "0.670");
            Assert.Equal("0.820", score.Text);
            Assert.Equal(.67, model.MinimumSupport);
            model.Method = PatternMatchingMethod.GeometricShape;
            PumpBindings();
            Assert.Equal("0.72", score.Text);
            TypeCharacters(score, "0.910");
            model.Method = PatternMatchingMethod.Shape;
            PumpBindings();
            Assert.Equal(.82, model.MinimumScore);
            Assert.Equal("0.82", score.Text);
            score.SetCurrentValue(TextBox.TextProperty, "bad");
            PumpBindings();
            Assert.True(Validation.GetHasError(score));
            model.Method = PatternMatchingMethod.GeometricShape;
            score.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
            Assert.Equal("0.910", score.Text);
            Assert.False(Validation.GetHasError(score));
        });
    }

    [Fact]
    public void PrimitiveThresholdSupportsByteRangeAndFractionalScore()
    {
        RunSta(() =>
        {
            using var model = new PrimitiveShapeDetectionViewModel(null!);
            var threshold = Bind(model, nameof(model.Threshold));
            TypeCharacters(threshold, "255");
            Assert.Equal((byte)255, model.Threshold);
            threshold.SetCurrentValue(TextBox.TextProperty, "256");
            PumpBindings();
            Assert.True(Validation.GetHasError(threshold));
            var score = Bind(model, nameof(model.MinimumScore));
            TypeCharacters(score, "0.850");
            Assert.Equal(.85, model.MinimumScore);
        });
    }

    [Fact]
    public void FractionalInputUsesTheBindingCulture()
    {
        RunSta(() =>
        {
            using var model = new ShapeMatchingViewModel(null!);
            var field = Bind(model, nameof(model.MinimumScore), CultureInfo.GetCultureInfo("fr-FR"));
            TypeCharacters(field, "0,720");
            Assert.Equal(.72, model.MinimumScore);
            Assert.False(Validation.GetHasError(field));
        });
    }

    private static TextBox Bind(object model, string property, CultureInfo? culture = null)
    {
        var field = new TextBox();
        field.SetBinding(TextBox.TextProperty, new Binding(property)
        {
            Source = model,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            Converter = new NumericTextConverter(),
            ConverterCulture = culture ?? CultureInfo.InvariantCulture,
            ValidatesOnExceptions = true
        });
        return field;
    }

    private static void TypeCharacters(TextBox field, string text)
    {
        field.SetCurrentValue(TextBox.TextProperty, "");
        var expected = "";
        foreach (var character in text)
        {
            field.CaretIndex = field.Text.Length;
            field.SelectedText = character.ToString();
            expected += character;
            PumpBindings();
            Assert.Equal(expected, field.Text);
        }
    }

    private static void PumpBindings()
    {
        Dispatcher.CurrentDispatcher.Invoke(() =>
        {
        }, DispatcherPriority.ContextIdle);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
