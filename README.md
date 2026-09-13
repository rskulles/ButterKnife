# ButterKnife

Chat with the models running on your own network. Download it, open it, point it at your Ollama or LM Studio box,
and start talking. Nothing leaves the house.

![A ButterKnife chat in dark mode: a question about counting word frequencies in Python and the model's answer with highlighted code](docs/screenshots/chat.png)

## Why ButterKnife

- **Download and go.** No Docker, no containers, no Python, no database to stand up. One file per platform and
  you're chatting in under a minute.
- **Plays nice with what you've got.** Already running Ollama or LM Studio? Anything that speaks the OpenAI API
  (vLLM, llama.cpp and friends)? ButterKnife just connects. Drop in an Anthropic key if you want Claude in the mix.
- **Your chats stay yours.** Everything lives in one file on your machine. The only thing ButterKnife ever talks to
  is the model servers you told it about.
- **Every screen in the house.** Run it on the desktop, open it on your phone or laptop over Wi‑Fi, and pick up the
  same chat anywhere. A QR code gets your phone there in one scan.
- **Fast and light.** No cloud round trips, no bloat. Replies land the instant your model starts talking.
- **Built for the long haul.** A little meter shows how much of the model's memory a chat has used, and ButterKnife
  quietly tidies up the older turns so a good conversation never hits a wall.

## Get it running

**1. Grab it** from the [Releases page](https://github.com/rskulles/ButterKnife/releases).

- **Windows:** unzip and run `ButterKnife.exe`. A butter knife shows up in the system tray; click it to open
  ButterKnife, right-click to quit. If Windows gives you the "unrecognised app" screen the first time, choose *More
  info*, then *Run anyway*.
- **macOS:** open the disk image (Apple Silicon or Intel) and drag ButterKnife to Applications. It lives in the menu
  bar, no Dock icon.
- **Linux:** unpack the archive and run `./ButterKnife`.

ButterKnife pops open in your browser. On Windows and Linux everything it saves sits in a `data` folder next to the
program, so backing it up or moving it is a copy and paste. On a Mac it's in `~/Library/Application Support/ButterKnife`.

**2. Point it at a model.** Go to **Settings → Connections** and press *Find servers on my network*; anything that
answers gets an *Add* button. Or pick a preset, type the address, hit *Test*, save.

- **Ollama** only talks to its own machine unless you tell it otherwise: set `OLLAMA_HOST=0.0.0.0` on that computer.
  The address is just the server, like `http://ollama.local:11434`.
- **LM Studio:** start the server in the Developer tab and turn on *Serve on local network*. The address ends in
  `/v1`, like `http://lmstudio.local:1234/v1`.
- **Anthropic** wants an API key from console.anthropic.com.
- **Whisper** (talk instead of type) works with whisper.cpp, Speaches, faster-whisper-server and LocalAI. The root
  address or the `/v1` one, either is fine.

**3. Use it from the couch (optional).** Under **Settings → General**, flip on *Reachable from other devices on the
local network* and restart ButterKnife. Then hit *Open on phone* in any chat and scan the code. Set a PIN on the same
page so only people who know it can open your chats from the Wi‑Fi; your own computer never has to type it.

## What you get

- **Personas.** Ready-made setups for the model: Assistant, Programmer, Writer, Teacher, Analyst, Product Describer,
  Copywriter and more. Pick one per chat and tweak the wording if you like.
- **Two models, one question.** Compare mode answers each prompt with two models side by side. Keep the one you like.
- **Pictures and files.** Drop, paste or attach up to six images per message for models that can see. If a model
  can't, ButterKnife says so and keeps the chat going. Text files and PDFs work with every model: their text goes
  to the model, and the chat shows a chip you can open to see what it read.
- **Second chances.** Edit a message and resend it, regenerate a reply, continue one that got cut off, or branch a
  chat off from any point and take it somewhere else.
- **Show your work.** Models that think out loud get a fold-out for their reasoning. Code comes highlighted with a
  copy button, and maths and diagrams render properly instead of as a wall of symbols.
- **Read it to me.** Any reply can be read aloud by your browser.
- **Talk instead of type.** The mic button listens, stops when you pause, and transcribes through your Whisper server,
  or through the browser's own speech recognition if you don't have one. From another device this needs an `https`
  address; that's the browser's rule, not ours.
- **Search.** A box at the top of the chat list finds any message or title across every chat and jumps straight to
  the message.
- **Light, dark, or match the system.** Remembered per browser.
- **Your name, not "User".** Set it once under Settings → General.

## Building from source

You'll need the .NET 10 SDK.

```bash
dotnet run --project src/ButterKnife
```

That starts it at <http://localhost:5175>. `dotnet test` runs the tests. The developer notes, the fake model server
for offline work, and how releases are built and signed are all in [CLAUDE.md](CLAUDE.md).

Bug reports and ideas are welcome; pull requests are not. [CONTRIBUTING.md](CONTRIBUTING.md) explains why, kindly.

## License

ButterKnife is **source-available, not open source**. It ships under the
[PolyForm Noncommercial License 1.0.0](LICENSE.md):

- Use it, copy it, change it and share it for **noncommercial** purposes: personal use, hobby projects, education,
  research, and use by charities, public institutions and the like.
- **Any commercial use needs written permission** from the copyright holder. Open an issue on this repository to ask.
- Every copy keeps the copyright notice and this license.

Copyright Roy S.

ButterKnife includes open-source components (the .NET runtime, Bootstrap, highlight.js, KaTeX, Mermaid, Markdig,
SQLite and others) under their own licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which comes with
every download.
