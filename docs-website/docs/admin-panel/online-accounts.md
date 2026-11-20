---
title: Online accounts
sidebar_position: 6
description: See who is playing, disconnect players and stop offline sessions.
---

# Online accounts

**Navigation:** *Online accounts* — route `/logged-in`

This page answers the question "who is on my server right now?" and lets you kick
somebody.

## Logged-in accounts

The first table lists every account which is currently logged in:

| Column | Meaning |
|---|---|
| Login name | The account which is logged in |
| Server id | The game server the account is playing on |
| Action | **Disconnect** — closes the connection of that account |

**Disconnect** is the tool of choice for a player who is misbehaving right now.
Combine it with a ban on the [Accounts page](accounts.md) if the player should not
come back.

## Active offline players

The second table lists the **offline sessions** — players who left their
character in the game world without being connected, for example to keep a
personal store open.

| Column | Meaning |
|---|---|
| Login name | The account of the offline session |
| Server id | The game server the session runs on |
| Started at | When the offline session started (UTC) |
| Action | **Stop** — ends the offline session |

Stopping an offline session removes that character from the game world, which
also closes its store.

## Bots

With the [bot feature](../server-features/bots.md) active, a third table lists the
server-side bots. Distributed game servers provide their status through the authenticated
administration channel; all game servers must run a version supporting the bot snapshot.

| Column | Meaning |
|---|---|
| Login name | The account the bot drives |
| Character | The character the bot plays |
| Server id | The game server which animates the bot |
| State | What the bot is doing right now, since when and why. A bot which announced an event meeting gets a second line: whether it is on its way or already waiting, the place — with `(safezone)` when it fell back to the safezone — and how long it still waits. After the meeting ended, that line says `Last meeting: joined`, `expired` or `abandoned` at the place; the tooltip explains why an abandoned meeting was given up, so a bot which promised to come and didn't can be checked later. |
| Loot decisions | The latest keep, equip, sell or store decision with its reason code and item, e.g. `store/market_candidate (Cheap)` or `sell/vendor_trash`. Hovering the cell lists the bot's recent decisions, newest first — the same reasons the debug log can sample, available without enabling debug logging. |
| Health | The number of restarts which healed the bot, or for how long it is quarantined from another restart. An empty cell means the bot is healthy. |
| Guild, Party | The guild and party of the bot, if any |
| Started at | When the bot session started (UTC) |
| Action | **Reset health** — forgets the failures, restarts and quarantine after the cause was fixed; only shown for in-process bots when there is something to forget |

The `bots_reset_health` operation can also clear retained records for stopped bots by
account name on in-process game servers. Health resets are not available for remote
game servers; the reset button is hidden for those rows. The table and `bots_list`
show active bots, not stopped health records.

A bot which waits at a meeting is not counted as stuck: it waits there on
purpose until the entrance closes. The table moves the countdown every second
and reads the bots again every few seconds, so an ended meeting turns into its
outcome and fresh loot decisions appear without a reload; a countdown of
`0:00 left` which stays like that means the bot's navigation stopped checking
the meeting, so the bot is worth a look.

### Bot market

With the bot merchants plugin active, a market card above the bot table shows
the market of every in-process game server: how many stores are open and how
many priced goods they offer, the goods listed, sold and retired in the last
hour and since the plugin started, the zen and paid items the bots took in, the
lowest, average and highest zen price of each class, and the configured
guardrails. The card refreshes every few seconds; a server without the plugin
has no card. The same numbers are available in game with `/botmarket`.

:::tip[Player counts]
The total number of connections per server is shown on the
[Servers page](servers.md), including a total over all game servers.
:::
