using VoiceChatbot;
using Xunit;

public class ServerContextWindowTests
{
    // GET /props from llama-server started with -c 16384 (one slot). dry_penalty_last_n and the
    // template are not context fields.
    private const string LlamaCppProps = """
        {
          "default_generation_settings": {
            "id": 0,
            "id_task": -1,
            "n_ctx": 16384,
            "speculative": false,
            "is_processing": false,
            "params": {
              "n_predict": -1,
              "seed": 4294967295,
              "temperature": 0.800000011920929,
              "top_k": 40,
              "top_p": 0.949999988079071,
              "min_p": 0.05000000074505806,
              "repeat_last_n": 64,
              "repeat_penalty": 1.0,
              "dry_penalty_last_n": 16384,
              "dry_sequence_breakers": ["\n", ":", "\"", "*"],
              "stop": [],
              "max_tokens": -1,
              "n_keep": 0,
              "n_discard": 0,
              "stream": true,
              "n_probs": 0,
              "chat_format": "Content-only",
              "reasoning_format": "auto",
              "samplers": ["penalties", "dry", "top_k", "typ_p", "top_p", "min_p", "xtc", "temperature"],
              "speculative.n_max": 16,
              "speculative.n_min": 0,
              "lora": []
            },
            "prompt": "",
            "next_token": {
              "has_next_token": true,
              "has_new_line": false,
              "n_remain": -1,
              "n_decoded": 0,
              "stopping_word": ""
            }
          },
          "total_slots": 1,
          "model_alias": "gemma-3-27b-it",
          "model_path": "C:\\models\\gemma-3-27b-it-Q4_K_M.gguf",
          "modalities": { "vision": true, "audio": false },
          "chat_template": "{{ bos_token }}{%- if messages[0]['role'] == 'system' -%}...",
          "bos_token": "<bos>",
          "eos_token": "<eos>",
          "build_info": "b6527-e6d65fb0"
        }
        """;

    // GET /slots from llama-server -c 16384 -np 2: each slot (each request) gets 8192.
    private const string LlamaCppSlots = """
        [
          {
            "id": 0, "id_task": -1, "n_ctx": 8192, "speculative": false, "is_processing": false,
            "params": { "n_predict": -1, "seed": 4294967295, "temperature": 0.8, "dry_penalty_last_n": 8192 },
            "prompt": "",
            "next_token": { "has_next_token": true, "has_new_line": false, "n_remain": -1, "n_decoded": 0, "stopping_word": "" }
          },
          {
            "id": 1, "id_task": 57, "n_ctx": 8192, "speculative": false, "is_processing": true,
            "params": { "n_predict": 512, "seed": 4294967295, "temperature": 0.7, "dry_penalty_last_n": 8192 },
            "prompt": "",
            "next_token": { "has_next_token": true, "has_new_line": true, "n_remain": 380, "n_decoded": 132, "stopping_word": "" }
          }
        ]
        """;

    // GET /v1/models from llama-server: meta.n_ctx_train is the model's training context, not -c.
    private const string LlamaCppModels = """
        {
          "models": [
            { "name": "gemma-3-27b-it", "model": "gemma-3-27b-it", "modified_at": "", "size": "", "digest": "",
              "type": "model", "description": "", "tags": [""], "capabilities": ["completion"], "parameters": "",
              "details": { "parent_model": "", "format": "gguf", "family": "", "families": [""], "parameter_size": "", "quantization_level": "" } }
          ],
          "object": "list",
          "data": [
            { "id": "gemma-3-27b-it", "object": "model", "created": 1758000000, "owned_by": "llamacpp",
              "meta": { "vocab_type": 1, "n_vocab": 262144, "n_ctx_train": 131072, "n_embd": 5376, "n_params": 27009002240, "size": 16541343744 } }
          ]
        }
        """;

    // GET /v1/models from vLLM started with --max-model-len 32768.
    private const string VllmModels = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen/Qwen2.5-14B-Instruct",
              "object": "model",
              "created": 1727000000,
              "owned_by": "vllm",
              "root": "Qwen/Qwen2.5-14B-Instruct",
              "parent": null,
              "max_model_len": 32768,
              "permission": [
                { "id": "modelperm-0b1c", "object": "model_permission", "created": 1727000000, "allow_create_engine": false,
                  "allow_sampling": true, "allow_logprobs": true, "allow_search_indices": false, "allow_view": true,
                  "allow_fine_tuning": false, "organization": "*", "group": null, "is_blocking": false }
              ]
            }
          ]
        }
        """;

    // POST /api/show from Ollama: model_info has the trained maximum; num_ctx is the Modelfile default.
    private const string OllamaShow = """
        {
          "license": "Gemma Terms of Use",
          "modelfile": "FROM gemma3:4b",
          "parameters": "num_ctx                        8192\nstop                           \"<end_of_turn>\"\ntemperature                    1",
          "template": "{{- range $i, $_ := .Messages }}...",
          "details": { "parent_model": "", "format": "gguf", "family": "gemma3", "families": ["gemma3"], "parameter_size": "4.3B", "quantization_level": "Q4_K_M" },
          "model_info": {
            "gemma3.attention.head_count": 8,
            "gemma3.block_count": 34,
            "gemma3.context_length": 131072,
            "gemma3.embedding_length": 2560,
            "general.architecture": "gemma3",
            "tokenizer.ggml.model": "llama"
          },
          "capabilities": ["completion", "vision"]
        }
        """;

    [Fact]
    public void LlamaCppPropsGiveThePerSlotWindow()
    {
        Assert.Equal(16384, ContextWindowParser.FromLlamaCppProps(LlamaCppProps, out var slots));
        Assert.Equal(1, slots);
    }

    [Fact]
    public void LlamaCppPropsAcceptSmallRealWindows()
    {
        Assert.Equal(2048, ContextWindowParser.FromLlamaCppProps("""{"default_generation_settings":{"n_ctx":2048},"total_slots":1}"""));
        Assert.Equal(512, ContextWindowParser.FromLlamaCppProps("""{"default_generation_settings":{"n_ctx":512}}"""));
        Assert.Null(ContextWindowParser.FromLlamaCppProps("""{"default_generation_settings":{"n_ctx":256}}"""));
    }

    [Fact]
    public void LlamaCppPropsWithoutRuntimeWindowGiveNothing()
    {
        Assert.Null(ContextWindowParser.FromLlamaCppProps("""{"default_generation_settings":{"n_ctx_train":131072},"total_slots":1}"""));
        Assert.Null(ContextWindowParser.FromLlamaCppProps("""{"error":{"code":404,"message":"File Not Found","type":"not_found_error"}}"""));
        Assert.Null(ContextWindowParser.FromLlamaCppProps("<html>Not found</html>"));
        Assert.Null(ContextWindowParser.FromLlamaCppProps(null));
    }

    [Fact]
    public void LlamaCppSlotsGiveTheSmallestSlot()
    {
        Assert.Equal(8192, ContextWindowParser.FromLlamaCppSlots(LlamaCppSlots, out var count));
        Assert.Equal(2, count);

        Assert.Equal(4096, ContextWindowParser.FromLlamaCppSlots("""[{"id":0,"n_ctx":8192},{"id":1,"n_ctx":4096}]""", out _));
        Assert.Null(ContextWindowParser.FromLlamaCppSlots("""{"error":{"code":501,"message":"This server does not support slots endpoint.","type":"not_supported_error"}}""", out _));
    }

    [Fact]
    public void TrainingContextIsNeverTheRuntimeWindow()
    {
        Assert.Null(ContextWindowParser.FromServerMetadata(LlamaCppModels, "gemma-3-27b-it"));
        Assert.Null(ContextWindowParser.FromServerMetadata("""{"max_position_embeddings":131072,"n_ctx_train":131072}""", null));
        // LM Studio's max_context_length is the model's maximum, not the length it was loaded with.
        Assert.Null(ContextWindowParser.FromServerMetadata(
            """{"object":"list","data":[{"id":"qwen2.5-7b-instruct","object":"model","type":"llm","state":"loaded","max_context_length":131072}]}""",
            "qwen2.5-7b-instruct"));
    }

    [Fact]
    public void VllmMaxModelLenIsTheRuntimeWindow()
    {
        Assert.Equal(32768, ContextWindowParser.FromServerMetadata(VllmModels, "Qwen/Qwen2.5-14B-Instruct"));
        // The only model in the list counts even when the name differs.
        Assert.Equal(32768, ContextWindowParser.FromServerMetadata(VllmModels, "qwen"));
        Assert.Equal("vLLM", ContextWindowParser.ServerNameFromModels(VllmModels));
        Assert.Equal("", ContextWindowParser.ServerNameFromModels(LlamaCppModels));
    }

    [Fact]
    public void ModelListsUseTheSelectedModelsEntry()
    {
        const string list = """
            {"data":[
              {"id":"small","loaded_context_length":4096,"max_context_length":32768},
              {"id":"big","loaded_context_length":16384,"max_context_length":131072}
            ]}
            """;
        Assert.Equal(16384, ContextWindowParser.FromServerMetadata(list, "big"));
        Assert.Equal(4096, ContextWindowParser.FromServerMetadata(list, "SMALL"));
        // Several models and none selected: unknown, rather than a guess.
        Assert.Null(ContextWindowParser.FromServerMetadata(list, "other"));
    }

    [Fact]
    public void OllamaShowGivesTheModelMaximum()
    {
        Assert.Equal(131072, ContextWindowParser.FromOllamaShow(OllamaShow));
        Assert.Equal(8192, ContextWindowParser.FromOllamaShow("""{"parameters":"num_ctx 8192\nstop \"<eos>\""}"""));
        Assert.Null(ContextWindowParser.FromOllamaShow("""{"error":"model 'nope' not found"}"""));
    }

    [Fact]
    public void ModelNamesAreOnlyGuessedForCloudApis()
    {
        Assert.True(ContextWindowParser.IsKnownCloudEndpoint("https://api.openai.com/v1"));
        Assert.True(ContextWindowParser.IsKnownCloudEndpoint("https://myorg.openai.azure.com/openai"));
        Assert.False(ContextWindowParser.IsKnownCloudEndpoint("http://192.168.1.50:8080/v1"));
        Assert.False(ContextWindowParser.IsKnownCloudEndpoint("http://localhost:1234/v1"));
        Assert.False(ContextWindowParser.IsKnownCloudEndpoint("not a url"));

        Assert.Null(ContextWindowParser.GuessCloudModelContext("http://192.168.1.50:8080/v1", "gemma-3-27b-it"));
        Assert.Null(ContextWindowParser.GuessCloudModelContext("http://192.168.1.50:8080/v1", "gpt-4o"));
        Assert.Null(ContextWindowParser.GuessCloudModelContext("https://generativelanguage.googleapis.com/v1beta/openai", "gemma-3-27b-it"));
        Assert.Equal(128_000, ContextWindowParser.GuessCloudModelContext("https://api.openai.com/v1", "gpt-4o-mini"));
        Assert.Equal(200_000, ContextWindowParser.GuessCloudModelContext("https://api.anthropic.com/v1", "claude-sonnet-4-5"));
    }

    [Fact]
    public void ServerWindowsWinButModelMaximumsAreCapped()
    {
        Assert.True(new ServerContextWindow(16384, ContextWindowSource.LlamaCppProps).IsServerWindow);
        Assert.True(new ServerContextWindow(8192, ContextWindowSource.LlamaCppSlots).IsServerWindow);
        Assert.True(new ServerContextWindow(32768, ContextWindowSource.ServerMetadata, "vLLM").IsServerWindow);
        Assert.True(new ServerContextWindow(4096, ContextWindowSource.ServerError).IsServerWindow);
        Assert.False(new ServerContextWindow(131072, ContextWindowSource.OllamaModel).IsServerWindow);
        Assert.False(new ServerContextWindow(128000, ContextWindowSource.KnownModel).IsServerWindow);
        Assert.False(ServerContextWindow.NotDetected(reachable: true).IsServerWindow);
    }

    [Fact]
    public void LlamaCppWindowIsUsedAsIsAndNotTheSetting()
    {
        var window = new ServerContextWindow(16384, ContextWindowSource.LlamaCppProps, ContextWindowParser.LlamaCppServerName, 1);
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(window.Tokens, 131072, window.IsServerWindow, fallback: 131072));
        Assert.Equal(16384, TokenBudget.ResolveContextWindow(window.Tokens, 4096, window.IsServerWindow, fallback: 131072));
    }

    [Fact]
    public void StatusLineSaysWhereTheWindowComesFrom()
    {
        var llama = new ServerContextWindow(16384, ContextWindowSource.LlamaCppProps, ContextWindowParser.LlamaCppServerName, 1);
        Assert.Equal($"llama.cpp server: {16384:N0} tokens per request (detected). Set it with -c on llama-server.",
            ContextWindowText.Describe(llama, configured: 16384, effective: 16384, isOllama: false, hasModel: true));

        var slots = new ServerContextWindow(8192, ContextWindowSource.LlamaCppSlots, ContextWindowParser.LlamaCppServerName, 4);
        Assert.Contains("4 parallel slots", ContextWindowText.Describe(slots, 16384, 8192, isOllama: false, hasModel: true));

        var ollama = new ServerContextWindow(131072, ContextWindowSource.OllamaModel, "Ollama");
        Assert.Equal($"Ollama: {16384:N0} tokens, sent as num_ctx (model max {131072:N0}).",
            ContextWindowText.Describe(ollama, 16384, 16384, isOllama: true, hasModel: true));

        Assert.Equal($"Not detected: using {16384:N0} from this box.",
            ContextWindowText.Describe(ServerContextWindow.NotDetected(reachable: true), 16384, 16384, isOllama: false, hasModel: true));
        Assert.Equal($"Server not reachable: using {131072:N0} (this box is 0).",
            ContextWindowText.Describe(ServerContextWindow.NotDetected(reachable: false), 0, 131072, isOllama: false, hasModel: true));

        var vllm = new ServerContextWindow(32768, ContextWindowSource.ServerMetadata, "vLLM");
        Assert.Contains("--max-model-len", ContextWindowText.Describe(vllm, 16384, 32768, isOllama: false, hasModel: true));

        var reported = new ServerContextWindow(4096, ContextWindowSource.ServerError, ContextWindowParser.LlamaCppServerName);
        Assert.Equal($"llama.cpp server: {4096:N0} tokens per request (reported by the server). Set it with -c on llama-server.",
            ContextWindowText.Describe(reported, 16384, 4096, isOllama: false, hasModel: true));
    }
}
