﻿using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Image;

public class SaveLayersOptions : PlatformOptionsBase
{
    public const string LayerIndexOptionName = "--layer-index";

    private readonly Argument<string> imageArg;
    private readonly Argument<string> outputPathArg;
    private readonly Option<bool> noSquashOption;
    private readonly Option<int?> layerIndexOption;

    public string Image { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool NoSquash { get; set; }
    public int? LayerIndex { get; set; }

    public SaveLayersOptions()
    {
        imageArg = Add(new Argument<string>("image") { Description = "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)" });
        outputPathArg = Add(new Argument<string>("output-path") { Description = "Path to the output location" });
        noSquashOption = Add(new Option<bool>("--no-squash") { Description = "Do not squash the image layers" });
        layerIndexOption = Add(new Option<int?>(LayerIndexOptionName) { Description = "Index of the image layer to target" });
    }

    protected override void GetValues()
    {
        base.GetValues();
        Image = GetValue(imageArg);
        OutputPath = GetValue(outputPathArg);
        NoSquash = GetValue(noSquashOption);
        LayerIndex = GetValue(layerIndexOption);
    }
}
