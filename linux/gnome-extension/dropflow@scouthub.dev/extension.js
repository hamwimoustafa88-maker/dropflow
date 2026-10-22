// SPDX-License-Identifier: GPL-3.0-or-later
// DropFlow drag helper for GNOME Shell 45-49 (Wayland).
//
// Under Wayland an application cannot see the pointer or the keyboard outside its own windows, so the DropFlow app cannot tell
// that a file is being dragged and a wheel key was tapped. GNOME Shell sees every drag. While one is in progress this extension
// writes short lines to the DropFlow app over the Unix socket $XDG_RUNTIME_DIR/dropflow/drag.sock (created by the app, mode 0600):
//
//     begin                     a drag entered the compositor
//     key shift|ctrl|alt X Y    that key went down during the drag; X,Y is the pointer in stage pixels
//     end                       the drag finished or was cancelled
//
// Nothing is sent when the app is not running (no socket), nothing is read but the modifier state and pointer position, and nothing
// ever leaves the computer.

import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import Clutter from 'gi://Clutter';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const POLL_INTERVAL_MS = 16;

const WHEEL_KEYS = [
    {name: 'shift', mask: Clutter.ModifierType.SHIFT_MASK},
    {name: 'ctrl', mask: Clutter.ModifierType.CONTROL_MASK},
    {name: 'alt', mask: Clutter.ModifierType.MOD1_MASK},
];

/** One socket connection per drag, so the app always sees begin … end in order. */
class DragChannel {
    constructor(path) {
        this._path = path;
        this._pending = [];
        this._output = null;
        this._connection = null;
        this._opening = false;
        this._closeWhenDrained = false;
    }

    send(line) {
        this._pending.push(`${line}\n`);
        if (this._output)
            this._drain();
        else if (!this._opening)
            this._open();
    }

    finish() {
        this._closeWhenDrained = true;
        if (this._output)
            this._drain();
        else if (!this._opening)
            this.close();
    }

    _open() {
        if (!GLib.file_test(this._path, GLib.FileTest.EXISTS)) {
            this._pending = [];
            return;
        }
        this._opening = true;
        const client = new Gio.SocketClient();
        client.connect_async(Gio.UnixSocketAddress.new(this._path), null, (source, result) => {
            this._opening = false;
            try {
                this._connection = source.connect_finish(result);
                this._output = this._connection.get_output_stream();
                this._drain();
            } catch (e) {
                this.close();
            }
        });
    }

    _drain() {
        const encoder = new TextEncoder();
        try {
            while (this._pending.length > 0)
                this._output.write_all(encoder.encode(this._pending.shift()), null);
        } catch (e) {
            this.close();
            return;
        }
        if (this._closeWhenDrained)
            this.close();
    }

    close() {
        try {
            this._connection?.close(null);
        } catch (e) {
            // already closed by the app
        }
        this._connection = null;
        this._output = null;
        this._pending = [];
    }
}

export default class DropFlowDragHelper extends Extension {
    enable() {
        this._dnd = global.backend.get_dnd();
        this._handlers = [
            this._dnd.connect('dnd-enter', () => this._dragStarted()),
            this._dnd.connect('dnd-leave', () => this._dragFinished()),
        ];
        this._timer = 0;
        this._channel = null;
    }

    disable() {
        if (this._dnd) {
            for (const id of this._handlers)
                this._dnd.disconnect(id);
        }
        this._handlers = [];
        this._dnd = null;
        this._stopTimer();
        this._channel?.close();
        this._channel = null;
    }

    _socketPath() {
        return GLib.build_filenamev([GLib.get_user_runtime_dir(), 'dropflow', 'drag.sock']);
    }

    _dragStarted() {
        if (this._timer)
            return;
        // Keys that are already down when the drag begins are not taps.
        const [, , modifiers] = global.get_pointer();
        this._previous = modifiers;
        this._channel = new DragChannel(this._socketPath());
        this._channel.send('begin');
        this._timer = GLib.timeout_add(GLib.PRIORITY_HIGH, POLL_INTERVAL_MS, () => {
            this._sample();
            return GLib.SOURCE_CONTINUE;
        });
    }

    _sample() {
        const [x, y, modifiers] = global.get_pointer();
        for (const key of WHEEL_KEYS) {
            const downNow = (modifiers & key.mask) !== 0;
            const downBefore = (this._previous & key.mask) !== 0;
            if (downNow && !downBefore)
                this._channel?.send(`key ${key.name} ${Math.round(x)} ${Math.round(y)}`);
        }
        this._previous = modifiers;
    }

    _dragFinished() {
        if (!this._timer)
            return;
        this._stopTimer();
        this._channel?.send('end');
        this._channel?.finish();
        this._channel = null;
    }

    _stopTimer() {
        if (this._timer)
            GLib.source_remove(this._timer);
        this._timer = 0;
    }
}
