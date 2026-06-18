# Install VoiceChatbot on an iPhone

## One-time Mac setup

1. Install current Xcode from the Mac App Store.
2. Open Xcode once and allow it to install additional components.
3. In Xcode, open **Settings > Accounts** and add your Apple ID.
4. Install Homebrew if it is not already installed:

   ```sh
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
   ```

5. Install XcodeGen:

   ```sh
   brew install xcodegen
   ```

## Get and open the iPhone project

```sh
git clone https://github.com/kkgagno/VoiceChatbot.git
cd VoiceChatbot
git switch codex/ios-native-client
cd ios
xcodegen generate
open VoiceChatbot.xcodeproj
```

In Xcode:

1. Select the **VoiceChatbot** project in the left sidebar.
2. Select the **VoiceChatbot** target.
3. Open **Signing & Capabilities**.
4. Enable **Automatically manage signing**.
5. Select your personal or paid Apple development team.
6. If Xcode says the bundle identifier is unavailable, change it to a unique value such as
   `com.yourname.VoiceChatbot`.

## Install on the iPhone

1. Connect the iPhone to the Mac with USB and unlock it.
2. Tap **Trust** if the phone asks whether to trust the Mac.
3. In Xcode's device picker, select your iPhone.
4. Press the Run button.
5. If requested, enable **Developer Mode** under
   **iPhone Settings > Privacy & Security > Developer Mode**, restart the phone, and run again.

A free Apple ID can install development builds, but they generally need to be re-signed
periodically. A paid Apple Developer account supports longer-lived development/TestFlight
distribution.

## Connect the app to the PC

On the Windows PC:

1. Start VoiceChatbot.
2. Enable **Phone Remote** and note its HTTPS URL and PIN.
3. Choose **Open Certificate Folder** and send the `.cer` file to the iPhone using AirDrop,
   iCloud Drive, email to yourself, or another private transfer.

On the iPhone:

1. Open Voice Chatbot and choose the **Connection** tab.
2. Enter the PC URL, including `https://` and port `5100`.
3. Enter the same PIN.
4. Tap **Import .cer certificate** and select the transferred certificate.
5. Tap **Save and test connection**.

Use the PC's LAN address while at home. When connected through VPN, use the PC address
reachable through that VPN. Windows Firewall must permit inbound TCP traffic on the selected
Phone Remote port for the applicable private/VPN network.

## Verify screen-lock operation

1. Start a conversation or transcription session.
2. Speak once while the screen is on and confirm that the PC transcribes it.
3. Lock the iPhone.
4. Speak again and wait for the PC response or transcript.
5. Use the Lock Screen media controls to pause, resume, or stop.

Calls, Siri, Bluetooth route changes, VPN loss, and force-quitting the app can interrupt or
end the audio session. The orange microphone privacy indicator remains visible while recording.

