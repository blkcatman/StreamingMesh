package com.streamingmesh.audio;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import androidx.media3.common.MediaItem;
import androidx.media3.common.MimeTypes;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.exoplayer.ExoPlayer;

/** HLS/fMP4 audio clock for the Unity receiver. All ExoPlayer access stays on Android's main looper. */
public final class StreamingMeshAndroidAudio {
    private static final String TAG = "StreamingMeshAudio";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Context context;
    private final MediaItem mediaItem;
    private ExoPlayer player;
    private volatile int state;
    private volatile long positionMs = -1;
    private volatile boolean released;

    private final Runnable updatePosition = new Runnable() {
        @Override public void run() {
            if (released || player == null) return;
            if (player.getPlaybackState() != Player.STATE_IDLE)
                positionMs = player.getCurrentPosition();
            handler.postDelayed(this, 20);
        }
    };

    public StreamingMeshAndroidAudio(Context context, String playlistUrl) {
        this.context = context.getApplicationContext();
        mediaItem = new MediaItem.Builder()
                .setUri(playlistUrl)
                .setMimeType(MimeTypes.APPLICATION_M3U8)
                .build();
        handler.post(this::createPlayer);
    }

    private void createPlayer() {
        if (released) return;
        player = new ExoPlayer.Builder(context).build();
        player.addListener(new Player.Listener() {
            @Override public void onPlaybackStateChanged(int playbackState) {
                state = playbackState == Player.STATE_READY ? 1 : 0;
                if (playbackState == Player.STATE_READY)
                    positionMs = player.getCurrentPosition();
            }

            @Override public void onPlayerError(PlaybackException error) {
                state = -1;
                positionMs = -1;
                Log.e(TAG, "HLS playback failed; retrying", error);
                handler.postDelayed(() -> {
                    if (released || player == null) return;
                    state = 0;
                    player.setMediaItem(mediaItem);
                    player.prepare();
                }, 2000);
            }
        });
        player.setPlayWhenReady(false);
        player.setMediaItem(mediaItem);
        player.prepare();
        handler.post(updatePosition);
    }

    public int getState() { return state; }

    public double getTimeSeconds() {
        long position = positionMs;
        return position < 0 ? -1.0 : position / 1000.0;
    }

    public void seek(double seconds) {
        long targetMs = Math.max(0, Math.round(seconds * 1000.0));
        positionMs = -1;
        handler.post(() -> {
            if (released || player == null) return;
            player.seekTo(targetMs);
            positionMs = player.getCurrentPosition();
        });
    }

    public void setPlaying(boolean playing) {
        handler.post(() -> {
            if (!released && player != null) player.setPlayWhenReady(playing);
        });
    }

    public void release() {
        released = true;
        handler.post(() -> {
            handler.removeCallbacks(updatePosition);
            if (player != null) {
                player.release();
                player = null;
            }
            state = 0;
            positionMs = -1;
        });
    }
}
