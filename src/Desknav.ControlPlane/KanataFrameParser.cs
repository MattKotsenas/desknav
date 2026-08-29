using System.Text.Json;

namespace Desknav.ControlPlane;

internal interface IKanataFrameParser
{
    KanataServerFrame Parse(string json);
}

internal sealed class KanataFrameParser : IKanataFrameParser
{
    public KanataServerFrame Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("LayerChange", out var layerChange))
        {
            return new KanataLayerChanged(
                KeyboardLayer.From(
                    layerChange.GetProperty("new").GetString()!));
        }

        if (root.TryGetProperty("MessagePush", out var messagePush))
        {
            var message = messagePush.GetProperty("message");
            if (message.ValueKind == JsonValueKind.Array
                && message.GetArrayLength() == 3
                && message[0].GetString() == "gesture")
            {
                return new KanataGesturePushed(
                    new GestureToken(
                        message[1].GetString()!,
                        message[2].GetString()!));
            }
        }

        throw new InvalidDataException("Unsupported Kanata TCP frame.");
    }
}
