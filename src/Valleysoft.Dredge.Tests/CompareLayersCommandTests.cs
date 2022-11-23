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
    public static object[][] GetJsonTestData()
    {
        return new object[][]
        {
            Scenarios.GetJsonEqualImagesTestData(includeHistory: false),
            Scenarios.GetJsonEqualImagesTestData(includeHistory: true),
            Scenarios.GetJsonDifferingImagesTestData(includeHistory: false),
            Scenarios.GetJsonDifferingImagesTestData(includeHistory: true),
            Scenarios.GetJsonRemovedLayerFromBaseTestData(),
            Scenarios.GetJsonAddedLayerTestData(),
            Scenarios.GetJsonDifferByHistoryOnlyTestData(includeHistory: false),
            Scenarios.GetJsonDifferByHistoryOnlyTestData(includeHistory: true),
            Scenarios.GetJsonWithHistoryDifferingByEmptyLayersTestData(includeHistory: false),
            Scenarios.GetJsonWithHistoryDifferingByEmptyLayersTestData(includeHistory: true)
        };
    }

    private static class Scenarios
    {
        // Identical images
        public static object[] GetJsonEqualImagesTestData(bool includeHistory)
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

            CompareLayersResult expectedResult = new(
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
                });

            return new object[]
            {
                nameof(GetJsonEqualImagesTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Same base layer, but next layer differs
        public static object[] GetJsonDifferingImagesTestData(bool includeHistory)
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

            CompareLayersResult expectedResult = new(
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
                });

            return new object[]
            {
                nameof(GetJsonDifferingImagesTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target removes a layer from base
        public static object[] GetJsonRemovedLayerFromBaseTestData()
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

            CompareLayersResult expectedResult = new(
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
                });

            return new object[]
            {
                nameof(GetJsonRemovedLayerFromBaseTestData),
                new CommandOptions(includeHistory: false),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // Target adds a layer
        public static object[] GetJsonAddedLayerTestData()
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

            CompareLayersResult expectedResult = new(
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
                });

            return new object[]
            {
                nameof(GetJsonAddedLayerTestData),
                new CommandOptions(includeHistory: false),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images only differ by history
        public static object[] GetJsonDifferByHistoryOnlyTestData(bool includeHistory)
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

            CompareLayersResult expectedResult = new(
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
                });

            return new object[]
            {
                nameof(GetJsonDifferByHistoryOnlyTestData),
                new CommandOptions(includeHistory),
                baseImageSetup,
                targetImageSetup,
                expectedResult
            };
        }

        // The images contain empty layers that differ in their history
        public static object[] GetJsonWithHistoryDifferingByEmptyLayersTestData(bool includeHistory)
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

            CompareLayersResult expectedResult = new(
                new CompareLayersSummary(areEqual: !includeHistory, targetIncludesAllBaseLayers: !includeHistory, lastCommonLayerIndex: includeHistory ? 0 : 1),
                comparisons);

            return new object[]
            {
                nameof(GetJsonWithHistoryDifferingByEmptyLayersTestData),
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
    [MemberData(nameof(GetJsonTestData))]
    public async void Json(
        string scenario, CommandOptions cmdOptions, ImageSetup baseImageSetup, ImageSetup targetImageSetup, CompareLayersResult expectedResult)
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
        Text text = (Text)await cmd.GetOutputAsync(
            baseImageName.ToString(),
            targetImageName.ToString(),
            CompareOutputFormat.Json,
            isColorDisabled: false,
            includeHistory: cmdOptions.IncludeHistory);

        CompareLayersResult actualResult = GetJson<CompareLayersResult>(text.GetSegments(AnsiConsole.Console));
        CompareJson(expectedResult, actualResult);
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