namespace IndustrialVision.Filters.Core;

public static class RecipeValidation
{
    public static ImageDescriptor Validate(Recipe recipe, ImageDescriptor input, ExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        foreach (var filter in recipe.Filters)
        {
            switch (filter)
            {
                case FlatField flat:
                    flat.Map.ValidateFor(input);
                    flat.Dark?.ValidateFor(input);
                    break;
                case Gaussian or Morphology or Threshold:
                    break;
                default:
                    throw new NotSupportedException($"Filter {filter.GetType().Name} has no implementation.");
            }
        }
        return input.WithFormat(recipe.Filters[^1] is Threshold ? PixelFormat.Gray8 : PixelFormat.Float32);
    }

    public static int RequiredHalo(Recipe recipe)
    {
        var halo = 0;
        for (var i = recipe.Filters.Count - 1; i >= 0; i--)
        {
            halo = checked(halo + (recipe.Filters[i] is Morphology morphology ? morphology.Halo : recipe.Filters[i].Radius));
        }
        return halo;
    }

    public static long CalibrationBytes(Recipe recipe) => recipe.Filters.OfType<FlatField>()
        .Sum(f => f.Map.Bytes + (f.Dark?.Bytes ?? 0));
}
