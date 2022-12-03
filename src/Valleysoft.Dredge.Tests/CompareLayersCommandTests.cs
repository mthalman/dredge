namespace Valleysoft.Dredge.Tests;

using Microsoft.Rest;
using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using Valleysoft.DockerRegistryClient.Models;
using LayersCommand = ImageCommand.CompareCommand.LayersCommand;

public class CompareLayersCommandTests
{
    private const string Registry = "test-registry.io";
    private static readonly ImageName baseImageName = ImageName.Parse($"{Registry}/base:latest");
    private static readonly ImageName targetImageName = ImageName.Parse($"{Registry}/target:latest");

    public static object[][] GetTestData(CompareLayersOutput format)
    {
        CommandOptions[] optionsSet = new[]
        {
            new CommandOptions(includeHistory: false, isColorDisabled: false),
            new CommandOptions(includeHistory: false, isColorDisabled: true),
            new CommandOptions(includeHistory: true, isColorDisabled: false),
            new CommandOptions(includeHistory: true, isColorDisabled: true)
        };

        List<object[]> testData = new();

        foreach (CommandOptions options in optionsSet)
        {
            testData.AddRange(new object[][]
            {
                Scenarios.GetEqualImagesTestData(format, options),
                Scenarios.GetDifferingImagesTestData(format, options),
                Scenarios.GetRemovedLayerFromBaseTestData(format, options),
                Scenarios.GetAddedLayerTestData(format, options),
                Scenarios.GetDifferByHistoryOnlyTestData(format, options),
                Scenarios.GetWithHistoryDifferingByEmptyLayersTestData(format, options),
            });
        }

        return testData.ToArray();
    }

    private static class Scenarios
    {
        // Identical images
        public static object[] GetEqualImagesTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                    new LayerHistory
                    {
                        CreatedBy = "a"
                    },
                    new LayerHistory
                    {
                        CreatedBy = "b"
                    }
                    }
                },
                new ManifestLayer[]
                {
                new ManifestLayer
                {
                    Digest = "layer-0"
                },
                new ManifestLayer
                {
                    Digest = "layer-1"
                }
            });

            ImageSetup targetImageSetup = baseImageSetup;

            object expectedResult = format switch
            {
                CompareLayersOutput.Json => new CompareLayersResult(
                    new CompareLayersSummary(areEqual: true, targetIncludesAllBaseLayers: true, lastCommonLayerIndex: 1),
                    new LayerComparison[]
                    {
                    new LayerComparison(
                        new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                        new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                        LayerDiff.Equal),
                    new LayerComparison(
                        new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                        new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                        LayerDiff.Equal)
                    }),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                      layer-1
                      b
                    """ : 
                    """
                      layer-0
                      layer-1
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        DigestRow("layer-1", "layer-1", options, LayerDiff.Equal),
                        HistoryRow("b", "b", options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetEqualImagesTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Same base layer, but next layer differs
        public static object[] GetDifferingImagesTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            ImageSetup targetImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1a"
                    }
                });

            object expectedResult = format switch
            {
                CompareLayersOutput.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: false, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                                new LayerInfo("layer-1a", options.IncludeHistory ? "b" : null),
                                LayerDiff.NotEqual)
                        }),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                    - layer-1
                    - b
                    + layer-1a
                    + b
                    """ :
                    """
                      layer-0
                    - layer-1
                    + layer-1a
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        DigestRow("layer-1", "layer-1a", options, LayerDiff.NotEqual),
                        HistoryRow("b", "b", options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetDifferingImagesTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target removes a layer from base
        public static object[] GetRemovedLayerFromBaseTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            ImageSetup targetImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    }
                });

            object expectedResult = format switch
            {
                CompareLayersOutput.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: false, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                                null,
                                LayerDiff.Removed)
                        }),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                    - layer-1
                    - b
                    """ :
                    """
                      layer-0
                    - layer-1
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        DigestRow("layer-1", string.Empty, options, LayerDiff.Removed),
                        HistoryRow("b", string.Empty, options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetRemovedLayerFromBaseTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target adds a layer
        public static object[] GetAddedLayerTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    }
                });

            ImageSetup targetImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            object expectedResult = format switch
            {
                CompareLayersOutput.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: true, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                null,
                                new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                                LayerDiff.Added)
                        }),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                    + layer-1
                    + b
                    """ :
                    """
                      layer-0
                    + layer-1
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        DigestRow(string.Empty, "layer-1", options, LayerDiff.Added),
                        HistoryRow(string.Empty, "b", options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetAddedLayerTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images only differ by history
        public static object[] GetDifferByHistoryOnlyTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            ImageSetup targetImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b1"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            object expectedResult = format switch
            {
                CompareLayersOutput.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(
                            areEqual: !options.IncludeHistory,
                            targetIncludesAllBaseLayers: !options.IncludeHistory,
                            lastCommonLayerIndex: options.IncludeHistory ? 0 : 1),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", options.IncludeHistory ? "b" : null),
                                new LayerInfo("layer-1", options.IncludeHistory ? "b1" : null),
                                options.IncludeHistory ? LayerDiff.NotEqual : LayerDiff.Equal)
                        }),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                    - layer-1
                    - b
                    + layer-1
                    + b1
                    """ :
                    """
                      layer-0
                      layer-1
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        DigestRow("layer-1", "layer-1", options, options.IncludeHistory ? LayerDiff.NotEqual : LayerDiff.Equal),
                        HistoryRow("b", "b1", options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetDifferByHistoryOnlyTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images contain empty layers that differ in their history
        public static object[] GetWithHistoryDifferingByEmptyLayersTestData(CompareLayersOutput format, CommandOptions options)
        {
            ImageSetup baseImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b",
                            IsEmptyLayer = true
                        },
                        new LayerHistory
                        {
                            CreatedBy = "c"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            ImageSetup targetImageSetup = new(
                new Image
                {
                    History = new LayerHistory[]
                    {
                        new LayerHistory
                        {
                            CreatedBy = "a"
                        },
                        new LayerHistory
                        {
                            CreatedBy = "b1",
                            IsEmptyLayer = true
                        },
                        new LayerHistory
                        {
                            CreatedBy = "c"
                        }
                    }
                },
                new ManifestLayer[]
                {
                    new ManifestLayer
                    {
                        Digest = "layer-0"
                    },
                    new ManifestLayer
                    {
                        Digest = "layer-1"
                    }
                });

            List<LayerComparison> comparisons = new()
            {
                new LayerComparison(
                    new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                    new LayerInfo("layer-0", options.IncludeHistory ? "a" : null),
                    LayerDiff.Equal),
                new LayerComparison(
                    new LayerInfo("layer-1", options.IncludeHistory ? "c" : null),
                    new LayerInfo("layer-1", options.IncludeHistory ? "c" : null),
                    LayerDiff.Equal)
            };

            if (options.IncludeHistory)
            {
                comparisons.Insert(1,
                    new LayerComparison(
                        new LayerInfo(null, "b"),
                        new LayerInfo(null, "b1"),
                        LayerDiff.NotEqual));
            }

            object expectedResult = format switch
            {
                CompareLayersOutput.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(
                            areEqual: !options.IncludeHistory,
                            targetIncludesAllBaseLayers: !options.IncludeHistory,
                            lastCommonLayerIndex: options.IncludeHistory ? 0 : 1),
                        comparisons),
                CompareLayersOutput.Inline => options.IncludeHistory ?
                    """
                      layer-0
                      a

                    - <empty layer>
                    - b
                    + <empty layer>
                    + b1

                      layer-1
                      c
                    """ :
                    """
                      layer-0
                      layer-1
                    """,
                CompareLayersOutput.SideBySide =>
                    ToArray(
                        DigestRow("layer-0", "layer-0", options, LayerDiff.Equal),
                        HistoryRow("a", "a", options),
                        EmptyRow(options),
                        options.IncludeHistory ?
                            DigestRow("<empty layer>", "<empty layer>", options, LayerDiff.NotEqual) :
                            null,
                        options.IncludeHistory ? HistoryRow("b", "b1", options) : null,
                        EmptyRow(options),
                        DigestRow("layer-1", "layer-1", options, LayerDiff.Equal),
                        HistoryRow("c", "c", options)
                    ),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetWithHistoryDifferingByEmptyLayersTestData),
                options,
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }
    }

    public class ImageSetup
    {
        public ImageSetup(Image image, ManifestLayer[] layers)
        {
            Image = image;
            Layers = layers;
        }

        public Image Image { get; }
        public ManifestLayer[] Layers { get; }
    }

    public class CommandOptions
    {
        public CommandOptions(bool includeHistory, bool isColorDisabled)
        {
            IncludeHistory = includeHistory;
            IsColorDisabled = isColorDisabled;
        }

        public bool IncludeHistory { get; }
        public bool IsColorDisabled { get; }
    }

    [Theory]
    [MemberData(nameof(GetTestData), CompareLayersOutput.Json)]
    public async void Json(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, CompareLayersResult expectedResult)
    {
        Text text = (Text)await ExecuteTestAsync(scenario, CompareLayersOutput.Json, cmdOptions, baseImageSetup, targetImageSetup);

        CompareLayersResult actualResult = GetJson<CompareLayersResult>(text.GetSegments(AnsiConsole.Console));
        CompareJson(expectedResult, actualResult);
    }

    [Theory]
    [MemberData(nameof(GetTestData), CompareLayersOutput.Inline)]
    public async void Inline(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, string expectedResult)
    {
        Rows rows = (Rows)await ExecuteTestAsync(scenario, CompareLayersOutput.Inline, cmdOptions, baseImageSetup, targetImageSetup);
        string actualResult = GetString(rows.GetSegments(AnsiConsole.Console));
        Assert.Equal(Normalize(expectedResult), Normalize(actualResult));
    }

    [Theory]
    [MemberData(nameof(GetTestData), CompareLayersOutput.SideBySide)]
    public async void SideBySide(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, string[][] expectedRows)
    {
        Table table = (Table)await ExecuteTestAsync(scenario, CompareLayersOutput.SideBySide, cmdOptions, baseImageSetup, targetImageSetup);

        Assert.Equal(cmdOptions.IsColorDisabled ? 3 : 2, table.Columns.Count);
        Assert.Equal(baseImageName.ToString(), GetString(table.Columns[0].Header.GetSegments(AnsiConsole.Console)));
        Assert.Equal(targetImageName.ToString(), GetString(table.Columns[^1].Header.GetSegments(AnsiConsole.Console)));

        Assert.Equal(expectedRows.Length, table.Rows.Count);

        for (int rowIndex = 0; rowIndex < expectedRows.Length; rowIndex++)
        {
            string[] expectedRowCells = expectedRows[rowIndex];
            TableRow actualRow = table.Rows.ElementAt(rowIndex);
            Assert.Equal(expectedRowCells.Length, actualRow.Count);

            for (int columnIndex = 0; columnIndex < expectedRowCells.Length; columnIndex++)
            {
                string expectedText = expectedRowCells[columnIndex];
                string actualText = GetString(actualRow[columnIndex].GetSegments(AnsiConsole.Console));
                Assert.Equal(expectedText, actualText);
            }
        }
    }

    private static string Normalize(string val) =>
        val.Replace("\r", string.Empty).TrimEnd();

    private static Task<IRenderable> ExecuteTestAsync(string scenario, CompareLayersOutput format, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup)
    {
        Assert.NotNull(scenario);

        Mock<IDockerRegistryClient> registryClientMock = new();

        SetupDockerRegistryClient(registryClientMock, baseImageName, baseImageSetup.Image, baseImageSetup.Layers);
        SetupDockerRegistryClient(registryClientMock, targetImageName, targetImageSetup.Image, targetImageSetup.Layers);

        Mock<IDockerRegistryClientFactory> clientFactoryMock = new();
        clientFactoryMock
            .Setup(o => o.GetClientAsync(Registry))
            .ReturnsAsync(registryClientMock.Object);

        LayersCommand cmd = new(clientFactoryMock.Object);
        return cmd.GetOutputAsync(
            baseImageName.ToString(),
            targetImageName.ToString(),
            format,
            isColorDisabled: cmdOptions.IsColorDisabled,
            includeHistory: cmdOptions.IncludeHistory);
    }

    private static void CompareJson<T>(T expected, T actual) =>
        Assert.Equal(JsonConvert.SerializeObject(expected), JsonConvert.SerializeObject(actual));

    private static T GetJson<T>(IEnumerable<Segment> segments) =>
        JsonConvert.DeserializeObject<T>(GetString(segments))!;

    private static string GetString(IEnumerable<Segment> segments)
    {
        StringBuilder builder = new();
        foreach (Segment segment in segments)
        {
            builder.Append(segment.Text);
        }
        return builder.ToString();
    }

    private static void SetupDockerRegistryClient(
        Mock<IDockerRegistryClient> registryClientMock, ImageName imageName, Image imageConfig, ManifestLayer[] layers)
    {
        string configDigest = $"{imageName.Repo}-config-digest";

        registryClientMock
            .Setup(o => o.Manifests.GetWithHttpMessagesAsync(imageName.Repo, imageName.Tag!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<ManifestInfo>()
            {
                Body = new ManifestInfo(string.Empty, string.Empty,
                    new DockerManifestV2
                    {
                        Config = new ManifestConfig
                        {
                            Digest = configDigest
                        },
                        Layers = layers
                    })
            });
        registryClientMock
            .Setup(o => o.Blobs.GetWithHttpMessagesAsync(imageName.Repo, configDigest, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpOperationResponse<Stream>
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(imageConfig)))
            });
    }

    private static string[] ToArray(params string?[] strings) =>
        strings
            .Where(str => str is not null)
            .Cast<string>()
            .ToArray();

    private static string[][] ToArray(params string[]?[] stringArrays) =>
        stringArrays
            .Where(str => str is not null)
            .Cast<string[]>()
            .ToArray();

    private static string[]? EmptyRow(CommandOptions options) =>
        options.IncludeHistory ?
            ToArray(string.Empty, options.IsColorDisabled ? string.Empty : null, string.Empty) :
            null;

    private static string[] DigestRow(string baseDigest, string targetDigest, CommandOptions options, LayerDiff diff) =>
        ToArray(baseDigest, options.IsColorDisabled ? GetDisplayName(diff) : null, targetDigest);

    private static string[]? HistoryRow(string baseHistory, string targetHistory, CommandOptions options) =>
        options.IncludeHistory ? ToArray(baseHistory, options.IsColorDisabled ? string.Empty : null, targetHistory) : null;

    private static string GetDisplayName(LayerDiff diff) =>
        diff switch
        {
            LayerDiff.Equal => "Equal",
            LayerDiff.NotEqual => "Not Equal",
            LayerDiff.Added => "Added",
            LayerDiff.Removed => "Removed",
            _ => throw new NotSupportedException()
        };
}
