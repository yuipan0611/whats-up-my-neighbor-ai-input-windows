namespace NekoChat.Core.Storage;

internal static class SecurityRevisionSchema
{
    internal const string Sql = """
        CREATE TABLE LocalSecurityRevision(singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0)) STRICT;
        INSERT INTO LocalSecurityRevision(singleton) VALUES(1);
        CREATE TABLE PeerSecurityRevision(peerId TEXT PRIMARY KEY NOT NULL CHECK(length(peerId) BETWEEN 1 AND 64), revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0)) STRICT;
        INSERT INTO PeerSecurityRevision(peerId) SELECT id FROM Peer;
        CREATE TABLE RosterSecurityRevision(rosterId TEXT PRIMARY KEY NOT NULL CHECK(length(rosterId) BETWEEN 1 AND 64), revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0)) STRICT;
        INSERT INTO RosterSecurityRevision(rosterId) SELECT id FROM RosterEntry;

        CREATE TRIGGER LocalSecurityRevision_monotonic BEFORE UPDATE OF revision ON LocalSecurityRevision WHEN NEW.revision<OLD.revision BEGIN SELECT RAISE(ABORT,'Security revision cannot decrease'); END;
        CREATE TRIGGER PeerSecurityRevision_monotonic BEFORE UPDATE OF revision ON PeerSecurityRevision WHEN NEW.revision<OLD.revision BEGIN SELECT RAISE(ABORT,'Security revision cannot decrease'); END;
        CREATE TRIGGER RosterSecurityRevision_monotonic BEFORE UPDATE OF revision ON RosterSecurityRevision WHEN NEW.revision<OLD.revision BEGIN SELECT RAISE(ABORT,'Security revision cannot decrease'); END;
        CREATE TRIGGER LocalSecurityRevision_no_delete BEFORE DELETE ON LocalSecurityRevision BEGIN SELECT RAISE(ABORT,'Security revision cannot be deleted'); END;
        CREATE TRIGGER PeerSecurityRevision_no_delete BEFORE DELETE ON PeerSecurityRevision BEGIN SELECT RAISE(ABORT,'Security revision cannot be deleted'); END;
        CREATE TRIGGER RosterSecurityRevision_no_delete BEFORE DELETE ON RosterSecurityRevision BEGIN SELECT RAISE(ABORT,'Security revision cannot be deleted'); END;
        CREATE TRIGGER LocalSecurityRevision_fixed_key BEFORE UPDATE OF singleton ON LocalSecurityRevision WHEN NEW.singleton IS NOT OLD.singleton BEGIN SELECT RAISE(ABORT,'Security revision key is immutable'); END;
        CREATE TRIGGER PeerSecurityRevision_fixed_key BEFORE UPDATE OF peerId ON PeerSecurityRevision WHEN NEW.peerId IS NOT OLD.peerId BEGIN SELECT RAISE(ABORT,'Security revision key is immutable'); END;
        CREATE TRIGGER RosterSecurityRevision_fixed_key BEFORE UPDATE OF rosterId ON RosterSecurityRevision WHEN NEW.rosterId IS NOT OLD.rosterId BEGIN SELECT RAISE(ABORT,'Security revision key is immutable'); END;

        CREATE TRIGGER LocalIdentity_security_insert AFTER INSERT ON LocalIdentity BEGIN UPDATE LocalSecurityRevision SET revision=revision+1 WHERE singleton=1; END;
        CREATE TRIGGER LocalIdentity_security_delete AFTER DELETE ON LocalIdentity BEGIN UPDATE LocalSecurityRevision SET revision=revision+1 WHERE singleton=1; END;
        CREATE TRIGGER LocalIdentity_security_update AFTER UPDATE OF id,publicKey,credentialRef ON LocalIdentity
        WHEN NEW.id IS NOT OLD.id OR NEW.publicKey IS NOT OLD.publicKey OR NEW.credentialRef IS NOT OLD.credentialRef
        BEGIN UPDATE LocalSecurityRevision SET revision=revision+1 WHERE singleton=1; END;

        CREATE TRIGGER Peer_security_insert AFTER INSERT ON Peer BEGIN
          INSERT INTO PeerSecurityRevision(peerId) VALUES(NEW.id) ON CONFLICT(peerId) DO UPDATE SET revision=revision+1;
        END;
        CREATE TRIGGER Peer_security_delete AFTER DELETE ON Peer BEGIN UPDATE PeerSecurityRevision SET revision=revision+1 WHERE peerId=OLD.id; END;
        CREATE TRIGGER Peer_security_update AFTER UPDATE OF pairingState,publicKey ON Peer
        WHEN NEW.pairingState IS NOT OLD.pairingState OR NEW.publicKey IS NOT OLD.publicKey
        BEGIN UPDATE PeerSecurityRevision SET revision=revision+1 WHERE peerId=NEW.id; END;
        CREATE TRIGGER Peer_security_id AFTER UPDATE OF id ON Peer WHEN NEW.id IS NOT OLD.id BEGIN
          UPDATE PeerSecurityRevision SET revision=revision+1 WHERE peerId=OLD.id;
          INSERT INTO PeerSecurityRevision(peerId) VALUES(NEW.id) ON CONFLICT(peerId) DO UPDATE SET revision=revision+1;
        END;

        CREATE TRIGGER Roster_security_insert AFTER INSERT ON RosterEntry BEGIN
          INSERT INTO RosterSecurityRevision(rosterId) VALUES(NEW.id) ON CONFLICT(rosterId) DO UPDATE SET revision=revision+1;
        END;
        CREATE TRIGGER Roster_security_delete AFTER DELETE ON RosterEntry BEGIN UPDATE RosterSecurityRevision SET revision=revision+1 WHERE rosterId=OLD.id; END;
        CREATE TRIGGER Roster_security_update AFTER UPDATE OF peerId,endpoint ON RosterEntry
        WHEN NEW.peerId IS NOT OLD.peerId OR NEW.endpoint IS NOT OLD.endpoint
        BEGIN UPDATE RosterSecurityRevision SET revision=revision+1 WHERE rosterId=NEW.id; END;
        CREATE TRIGGER Roster_security_id AFTER UPDATE OF id ON RosterEntry WHEN NEW.id IS NOT OLD.id BEGIN
          UPDATE RosterSecurityRevision SET revision=revision+1 WHERE rosterId=OLD.id;
          INSERT INTO RosterSecurityRevision(rosterId) VALUES(NEW.id) ON CONFLICT(rosterId) DO UPDATE SET revision=revision+1;
        END;
        """;
}
