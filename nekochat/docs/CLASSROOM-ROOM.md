# Classroom room and IP addressing

Implementation plan (2026-10-27): replace the Lite default two-person flow with one shared classroom room. One host shares one invitation, up to 100 PCs join, and messages remain directed to the recipient IP. No room-wide broadcast or ICMP/network scanning is added.

Impact: `ClassroomHost`, `App.Classroom`, chat addressing and the bundled desktop Relay. Add a separate versioned classroom admission route and invitation; preserve old `NEKO1` parsing and paired Relay behavior for regression tests. Keep the v1 chat WebSocket schema, static LAN/VPS friend policy, Groq credential storage and installer identity. No changes to legacy Neighbor, macOS, Agent/control routes, deployment or main.

The admission boundary is security-sensitive. Sharing a classroom invitation authorizes members of that room to exchange text. IP is an address, not a password or proof of a person's identity. The Relay derives each guest's address from the direct TCP connection, issues a distinct ephemeral token, binds it to that IP and rejects duplicate addresses. It ignores forwarded-IP headers. The host keeps its separate credential. Students must already have joined before they can receive a message.

Verification sequence: baseline C# and Relay suites; focused admission/auth/IP tests and three-client directed delivery; C# invitation/addressing checks; WPF skip/room/recipient/reply controls; bundled Windows Relay and installer CI. Classroom physical LAN/AP isolation and school IP assignment require onsite acceptance.


Confirmed display behavior: students send to the teacher's IP, and only the teacher's App receives those packets. Teams screen sharing makes the teacher's display visible to the class. Students can also send directly to another joined member by IP; neither the teacher UI nor other peers receive a copy. There is no broadcast event. The teacher is the room host; joining selects the teacher by default and the Ping teacher action sends the current nonempty draft to that host. Incoming bubble notifications queue with their original sender for click-to-reply; recent history remains visible in the chat page.
