using VoiceChatbot;
using Xunit;

public class MediaIntentParserTests
{
    // ---- Create image -------------------------------------------------------------------------

    [Theory]
    [InlineData("Create an image of a cat on the moon", "a cat on the moon")]
    [InlineData("create image of a red barn", "a red barn")]
    [InlineData("Generate an image showing a sunset over the ocean", "a sunset over the ocean")]
    [InlineData("Make an image with a red sports car", "a red sports car")]
    [InlineData("Draw a picture of my dog wearing a hat", "my dog wearing a hat")]
    [InlineData("please create a picture of a lighthouse", "a lighthouse")]
    [InlineData("Generate a photo of a mountain lake at dawn", "a mountain lake at dawn")]
    [InlineData("Create an image: a castle in the clouds", "a castle in the clouds")]
    [InlineData("Generate image, neon city at night", "neon city at night")]
    [InlineData("Create another image of the same cat", "the same cat")]
    [InlineData("Create an image of a QR code poster", "a QR code poster")]
    [InlineData("  Create an image of a fox.  ", "a fox.")]
    // Picture subjects that share a word with a code tool still make an image.
    [InlineData("Create an image of a mermaid on a rock", "a mermaid on a rock")]
    [InlineData("Draw a picture of my daughter as a mermaid", "my daughter as a mermaid")]
    [InlineData("Generate an image of a cat asleep on a pillow", "a cat asleep on a pillow")]
    [InlineData("Create an image of an old robot covered in rust", "an old robot covered in rust")]
    [InlineData("Create a picture of a sofa with pillows", "a sofa with pillows")]
    public void ImageCreate_ExplicitPhrasing(string text, string expectedPrompt)
    {
        Assert.True(MediaIntentParser.TryGetImageCreatePrompt(text, out var prompt));
        Assert.Equal(expectedPrompt, prompt);
    }

    [Theory]
    [InlineData("Create a movie review of Dune")]
    [InlineData("Create an image classifier in Python")]
    [InlineData("Create an image gallery component in React")]
    [InlineData("Make a picture frame component")]
    [InlineData("Create a picture-in-picture video player")]
    [InlineData("Create a Docker image for this app")]
    [InlineData("Draw a picture of a cat in SVG")]
    [InlineData("Generate an image with Python PIL that shows a bar chart")]
    [InlineData("Make an image of the architecture as a mermaid diagram")]
    [InlineData("Draw a picture of the login flow in Mermaid")]
    [InlineData("Generate an image with Pillow that shows a gradient")]
    [InlineData("Create a photo album app")]
    [InlineData("What makes an image look professional?")]
    [InlineData("Create an image")]
    [InlineData("Create an image of")]
    [InlineData("Draw me a cat")]
    [InlineData("")]
    [InlineData("Create an image of this video https://youtu.be/dQw4w9WgXcQ")]
    public void ImageCreate_IgnoresOrdinaryRequests(string text)
    {
        Assert.False(MediaIntentParser.TryGetImageCreatePrompt(text, out var prompt));
        Assert.Equal("", prompt);
    }

    // ---- Edit image ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Edit this image and make the sky purple", "make the sky purple")]
    [InlineData("edit image to add a hat", "add a hat")]
    [InlineData("Edit the image to add sunglasses", "add sunglasses")]
    [InlineData("Edit this photo: remove the person on the left", "remove the person on the left")]
    [InlineData("please edit this picture and turn it into a painting", "turn it into a painting")]
    [InlineData("Edit this image make it black and white", "make it black and white")]
    [InlineData("Edit the image so the dog is smiling", "so the dog is smiling")]
    [InlineData("Edit my photo by adding snow", "by adding snow")]
    [InlineData("Change this image to a watercolor style", "a watercolor style")]
    [InlineData("Change the photo into a pencil sketch", "into a pencil sketch")]
    [InlineData("Modify this picture and add a rainbow", "add a rainbow")]
    [InlineData("modify the image, replace the car with a bike", "replace the car with a bike")]
    [InlineData("Edit the last generated image to add a moon", "add a moon")]
    [InlineData("Edit these images and blend them together", "blend them together")]
    public void ImageEdit_ExplicitPhrasing(string text, string expectedPrompt)
    {
        Assert.True(MediaIntentParser.TryGetImageEditPrompt(text, out var prompt));
        Assert.Equal(expectedPrompt, prompt);
    }

    [Theory]
    [InlineData("Edit the code above to use async")]
    [InlineData("Edit my essay to be more formal")]
    [InlineData("edit this to be shorter")]
    [InlineData("Edit the image upload handler to support PNG")]
    [InlineData("Change the image size in CSS")]
    [InlineData("Change the image to grayscale in CSS")]
    [InlineData("Modify the image processing pipeline to use OpenCV")]
    [InlineData("Change the picture-perfect intro paragraph")]
    [InlineData("Edit the image")]
    [InlineData("Update the image to node:18-alpine")]
    [InlineData("Edit this photo of https://youtu.be/dQw4w9WgXcQ to add a hat")]
    public void ImageEdit_IgnoresOrdinaryRequests(string text)
    {
        Assert.False(MediaIntentParser.TryGetImageEditPrompt(text, out _));
    }

    // ---- Video --------------------------------------------------------------------------------

    [Theory]
    [InlineData("Create a video of a cat surfing", "a cat surfing", null)]
    [InlineData("Generate a 5 second video of waves crashing", "waves crashing", 5)]
    [InlineData("make a 10-second clip of a sunset", "a sunset", 10)]
    [InlineData("Create an 8 seconds long video showing the dog running", "the dog running", 8)]
    [InlineData("Make a 6s video of fireworks", "fireworks", 6)]
    [InlineData("Create a video where she waves at the camera", "she waves at the camera", null)]
    [InlineData("make a movie of a rocket launch", "a rocket launch", null)]
    [InlineData("Create a video: slow zoom on the mountains", "slow zoom on the mountains", null)]
    [InlineData("please generate a clip with the camera panning left", "the camera panning left", null)]
    [InlineData("Create a video from this image where the leaves fall", "from this image where the leaves fall", null)]
    [InlineData("Turn this image into a video where she smiles", "she smiles", null)]
    [InlineData("turn this picture into a 4 second clip of the waves moving", "the waves moving", 4)]
    [InlineData("5 second video of a cat dancing", "a cat dancing", 5)]
    [InlineData("Can you make me a 5 second video of my cat jumping?", "my cat jumping?", 5)]
    [InlineData("Generate a 45 second video of rain", "rain", 30)]
    [InlineData("Create a video of a mermaid swimming", "a mermaid swimming", null)]
    public void Video_ExplicitPhrasing(string text, string expectedPrompt, int? expectedSeconds)
    {
        Assert.True(MediaIntentParser.TryGetVideoPrompt(text, out var prompt, out var seconds));
        Assert.Equal(expectedPrompt, prompt);
        Assert.Equal(expectedSeconds, seconds);
    }

    [Theory]
    [InlineData("Turn this image into a video")]
    [InlineData("Turn this image into a video.")]
    [InlineData("Make the photo into a movie")]
    [InlineData("convert my picture into a 5 second clip")]
    public void Video_ImageIntoVideoWithoutDescription(string text)
    {
        // No description after the noun: the prompt falls back to the request minus the command words.
        Assert.True(MediaIntentParser.TryGetVideoPrompt(text, out var prompt, out _));
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    [Theory]
    // Worked before the stricter parser (with an attached image) and must keep working.
    [InlineData("Make a 5 second video", 5)]
    [InlineData("generate a 10-second clip.", 10)]
    [InlineData("Create a 6s video", 6)]
    [InlineData("Make a video from this", null)]
    [InlineData("Create a 5 second video from it.", 5)]
    public void Video_LengthOrSourceWithoutDescription(string text, int? expectedSeconds)
    {
        Assert.True(MediaIntentParser.TryGetVideoPrompt(text, out var prompt, out var seconds));
        Assert.Equal(expectedSeconds, seconds);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.NotEqual(".", prompt);
    }

    [Theory]
    [InlineData("Summarize Veritasium's video https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("Summarize this video https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("Make a 5 second video summary of https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("Create a video of https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("Summarize Veritasium's video about lasers")]
    [InlineData("Create a movie review of Dune")]
    [InlineData("Create a video script about our product launch")]
    [InlineData("Create a video game in Python")]
    [InlineData("Make a clip art style logo")]
    [InlineData("I made a 10 second video yesterday, is that long enough?")]
    [InlineData("Can you make a 30 second video summary of this article?")]
    [InlineData("What's a good video editor?")]
    [InlineData("Create a video")]
    [InlineData("Recommend a movie with Tom Hanks")]
    [InlineData("Generate a 90s style video of a diner")]
    [InlineData("Make this image into a video game")]
    [InlineData("Turn this picture into a movie poster")]
    [InlineData("Create a video from this article")]
    [InlineData("Make a 5 second video summary")]
    public void Video_IgnoresOrdinaryRequests(string text)
    {
        Assert.False(MediaIntentParser.TryGetVideoPrompt(text, out var prompt, out var seconds));
        Assert.Equal("", prompt);
        Assert.Null(seconds);
    }

    [Theory]
    [InlineData("a cat 5 seconds long", 5)]
    [InlineData("10-second clip", 10)]
    [InlineData("3 sec", 3)]
    [InlineData("7s video", 7)]
    [InlineData("99 seconds", 30)]
    [InlineData("0 seconds", 1)]
    [InlineData("a 90s arcade", null)]
    [InlineData("3 dogs and 2 sisters", null)]
    [InlineData("", null)]
    public void ParseVideoSeconds(string text, int? expected) =>
        Assert.Equal(expected, MediaIntentParser.TryParseVideoSeconds(text));
}
