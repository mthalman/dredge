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
    public static object[][] GetTestData(CompareOutputFormat format)
    {
        return new object[][]
        {
            Scenarios.GetEqualImagesTestData(format, includeHistory: false),
            Scenarios.GetEqualImagesTestData(format, includeHistory: true),
            Scenarios.GetDifferingImagesTestData(format, includeHistory: false),
            Scenarios.GetDifferingImagesTestData(format, includeHistory: true),
            Scenarios.GetRemovedLayerFromBaseTestData(format),
            Scenarios.GetAddedLayerTestData(format),
            Scenarios.GetDifferByHistoryOnlyTestData(format, includeHistory: false),
            Scenarios.GetDifferByHistoryOnlyTestData(format, includeHistory: true),
            Scenarios.GetWithHistoryDifferingByEmptyLayersTestData(format, includeHistory: false),
            Scenarios.GetWithHistoryDifferingByEmptyLayersTestData(format, includeHistory: true)
        };
    }

    private static class Scenarios
    {
        // Identical images
        public static object[] GetEqualImagesTestData(CompareOutputFormat format, bool includeHistory)
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
                CompareOutputFormat.Json => new CompareLayersResult(
                    new CompareLayersSummary(areEqual: true, targetIncludesAllBaseLayers: true, lastCommonLayerIndex: 1),
                    new LayerComparison[]
                    {
                    new LayerComparison(
                        new LayerInfo("layer-0", includeHistory ? "a" : null),
                        new LayerInfo("layer-0", includeHistory ? "a" : null),
                        LayerDiff.Equal),
                    new LayerComparison(
                        new LayerInfo("layer-1", includeHistory ? "b" : null),
                        new LayerInfo("layer-1", includeHistory ? "b" : null),
                        LayerDiff.Equal)
                    }),
                CompareOutputFormat.Inline => includeHistory ?
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
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetEqualImagesTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Same base layer, but next layer differs
        public static object[] GetDifferingImagesTestData(CompareOutputFormat format, bool includeHistory)
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
                CompareOutputFormat.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: false, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", includeHistory ? "a" : null),
                                new LayerInfo("layer-0", includeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", includeHistory ? "b" : null),
                                new LayerInfo("layer-1a", includeHistory ? "b" : null),
                                LayerDiff.NotEqual)
                        }),
                CompareOutputFormat.Inline => includeHistory ?
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
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetDifferingImagesTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target removes a layer from base
        public static object[] GetRemovedLayerFromBaseTestData(CompareOutputFormat format)
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
                CompareOutputFormat.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: false, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", null),
                                new LayerInfo("layer-0", null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", null),
                                null,
                                LayerDiff.Removed)
                        }),
                CompareOutputFormat.Inline =>
                    """
                      layer-0
                    - layer-1
                    """,
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetRemovedLayerFromBaseTestData),
                new CommandOptions(includeHistory: false),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target adds a layer
        public static object[] GetAddedLayerTestData(CompareOutputFormat format)
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
                CompareOutputFormat.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: false, targetIncludesAllBaseLayers: true, lastCommonLayerIndex: 0),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", null),
                                new LayerInfo("layer-0", null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                null,
                                new LayerInfo("layer-1", null),
                                LayerDiff.Added)
                        }),
                CompareOutputFormat.Inline =>
                    """
                      layer-0
                    + layer-1
                    """,
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetAddedLayerTestData),
                new CommandOptions(includeHistory: false),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images only differ by history
        public static object[] GetDifferByHistoryOnlyTestData(CompareOutputFormat format, bool includeHistory)
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
                CompareOutputFormat.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(
                            areEqual: !includeHistory,
                            targetIncludesAllBaseLayers: !includeHistory,
                            lastCommonLayerIndex: includeHistory ? 0 : 1),
                        new LayerComparison[]
                        {
                            new LayerComparison(
                                new LayerInfo("layer-0", includeHistory ? "a" : null),
                                new LayerInfo("layer-0", includeHistory ? "a" : null),
                                LayerDiff.Equal),
                            new LayerComparison(
                                new LayerInfo("layer-1", includeHistory ? "b" : null),
                                new LayerInfo("layer-1", includeHistory ? "b1" : null),
                                includeHistory ? LayerDiff.NotEqual : LayerDiff.Equal)
                        }),
                CompareOutputFormat.Inline => includeHistory ?
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
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetDifferByHistoryOnlyTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images contain empty layers that differ in their history
        public static object[] GetWithHistoryDifferingByEmptyLayersTestData(CompareOutputFormat format, bool includeHistory)
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
                    new LayerInfo("layer-0", includeHistory ? "a" : null),
                    new LayerInfo("layer-0", includeHistory ? "a" : null),
                    LayerDiff.Equal),
                new LayerComparison(
                    new LayerInfo("layer-1", includeHistory ? "c" : null),
                    new LayerInfo("layer-1", includeHistory ? "c" : null),
                    LayerDiff.Equal)
            };

            if (includeHistory)
            {
                comparisons.Insert(1,
                    new LayerComparison(
                        new LayerInfo(null, "b"),
                        new LayerInfo(null, "b1"),
                        LayerDiff.NotEqual));
            }

            object expectedResult = format switch
            {
                CompareOutputFormat.Json =>
                    new CompareLayersResult(
                        new CompareLayersSummary(areEqual: !includeHistory, targetIncludesAllBaseLayers: !includeHistory, lastCommonLayerIndex: includeHistory ? 0 : 1),
                        comparisons),
                CompareOutputFormat.Inline => includeHistory ?
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
                CompareOutputFormat.SideBySide => new object(),
                _ => throw new NotSupportedException()
            };

            return new object[]
            {
                nameof(GetWithHistoryDifferingByEmptyLayersTestData),
                new CommandOptions(includeHistory),
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
        public CommandOptions(bool includeHistory)
        {
            IncludeHistory = includeHistory;
        }

        public bool IncludeHistory { get; }
    }

    [Theory]
    [MemberData(nameof(GetTestData), CompareOutputFormat.Json)]
    public async void Json(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, CompareLayersResult expectedResult)
    {
        Text text = (Text)await ExecuteTestAsync(scenario, CompareOutputFormat.Json, cmdOptions, baseImageSetup, targetImageSetup);

        CompareLayersResult actualResult = GetJson<CompareLayersResult>(text.GetSegments(AnsiConsole.Console));
        CompareJson(expectedResult, actualResult);
    }

    [Theory]
    [MemberData(nameof(GetTestData), CompareOutputFormat.Inline)]
    public async void Inline(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, string expectedResult)
    {
        Rows rows = (Rows)await ExecuteTestAsync(scenario, CompareOutputFormat.Inline, cmdOptions, baseImageSetup, targetImageSetup);
        string actualResult = GetString(rows.GetSegments(AnsiConsole.Console));
        Assert.Equal(Normalize(expectedResult), Normalize(actualResult));
    }

    private static string Normalize(string val) =>
        val.Replace("\r", string.Empty).TrimEnd();

    private static Task<IRenderable> ExecuteTestAsync(string scenario, CompareOutputFormat format, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup)
    {
        Assert.NotNull(scenario);
        const string Registry = "test-registry.io";
        ImageName baseImageName = ImageName.Parse($"{Registry}/base:latest");
        ImageName targetImageName = ImageName.Parse($"{Registry}/target:latest");

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
            isColorDisabled: false,
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
}