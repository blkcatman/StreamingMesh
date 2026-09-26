#import <AVFoundation/AVFoundation.h>
#import <Foundation/Foundation.h>

#include <stdint.h>

@interface STMAppleAudioPlayer : NSObject
@property(nonatomic, strong) AVPlayer *player;
@property(nonatomic, assign) BOOL ended;
- (instancetype)initWithPlaylistURL:(NSURL *)url;
- (double)playbackTime;
- (int)playbackState;
- (void)seekToSeconds:(double)seconds;
@end

@implementation STMAppleAudioPlayer

- (instancetype)initWithPlaylistURL:(NSURL *)url
{
  self = [super init];
  if (self)
  {
    AVPlayerItem *item = [AVPlayerItem playerItemWithURL:url];
    _player = [AVPlayer playerWithPlayerItem:item];
    _player.automaticallyWaitsToMinimizeStalling = YES;
    _ended = NO;
    [[NSNotificationCenter defaultCenter]
      addObserver:self
      selector:@selector(itemDidReachEnd:)
      name:AVPlayerItemDidPlayToEndTimeNotification
      object:item];
    [_player play];
  }
  return self;
}

- (void)dealloc
{
  [[NSNotificationCenter defaultCenter] removeObserver:self];
  [_player pause];
}

- (void)itemDidReachEnd:(NSNotification *)notification
{
  _ended = YES;
}

- (double)playbackTime
{
  AVPlayerItem *item = _player.currentItem;
  if (item == nil || item.status != AVPlayerItemStatusReadyToPlay)
    return -1.0;

  double seconds = CMTimeGetSeconds(_player.currentTime);
  return isfinite(seconds) && seconds >= 0.0 ? seconds : -1.0;
}

- (int)playbackState
{
  AVPlayerItem *item = _player.currentItem;
  if (item == nil)
    return 0;
  if (item.status == AVPlayerItemStatusFailed)
    return -1;
  if (item.status != AVPlayerItemStatusReadyToPlay)
    return 0;
  if (_ended)
    return 3;
  if (_player.timeControlStatus == AVPlayerTimeControlStatusPlaying)
    return 2;
  return 1;
}

- (void)seekToSeconds:(double)seconds
{
  _ended = NO;
  CMTime time = CMTimeMakeWithSeconds(MAX(0.0, seconds), 1000000);
  [_player seekToTime:time toleranceBefore:kCMTimeZero toleranceAfter:kCMTimeZero];
  [_player play];
}

@end

static NSMutableDictionary<NSNumber *, STMAppleAudioPlayer *> *STMPlayers(void)
{
  static NSMutableDictionary<NSNumber *, STMAppleAudioPlayer *> *players;
  static dispatch_once_t onceToken;
  dispatch_once(&onceToken, ^{
    players = [[NSMutableDictionary alloc] init];
  });
  return players;
}

static int32_t STMNextHandle(void)
{
  static int32_t nextHandle = 1;
  return nextHandle++;
}

static void STMRunOnMain(dispatch_block_t block)
{
  if ([NSThread isMainThread])
    block();
  else
    dispatch_sync(dispatch_get_main_queue(), block);
}

extern "C"
{
  __attribute__((visibility("default")))
  int32_t STM_AppleAudio_Create(const char *playlistURL)
  {
    if (playlistURL == nullptr)
      return 0;

    __block int32_t handle = 0;
    NSString *urlString = [NSString stringWithUTF8String:playlistURL];
    NSURL *url = urlString != nil ? [NSURL URLWithString:urlString] : nil;
    if (url == nil)
      return 0;

    STMRunOnMain(^{
      handle = STMNextHandle();
      STMPlayers()[@(handle)] = [[STMAppleAudioPlayer alloc] initWithPlaylistURL:url];
    });
    return handle;
  }

  __attribute__((visibility("default")))
  double STM_AppleAudio_GetTime(int32_t handle)
  {
    __block double time = -1.0;
    STMRunOnMain(^{
      STMAppleAudioPlayer *entry = STMPlayers()[@(handle)];
      if (entry != nil)
        time = [entry playbackTime];
    });
    return time;
  }

  __attribute__((visibility("default")))
  int32_t STM_AppleAudio_GetState(int32_t handle)
  {
    __block int32_t state = 0;
    STMRunOnMain(^{
      STMAppleAudioPlayer *entry = STMPlayers()[@(handle)];
      if (entry != nil)
        state = [entry playbackState];
    });
    return state;
  }

  __attribute__((visibility("default")))
  void STM_AppleAudio_Seek(int32_t handle, double time)
  {
    STMRunOnMain(^{
      STMAppleAudioPlayer *entry = STMPlayers()[@(handle)];
      if (entry != nil)
        [entry seekToSeconds:time];
    });
  }

  __attribute__((visibility("default")))
  void STM_AppleAudio_Destroy(int32_t handle)
  {
    STMRunOnMain(^{
      STMAppleAudioPlayer *entry = STMPlayers()[@(handle)];
      [entry.player pause];
      [STMPlayers() removeObjectForKey:@(handle)];
    });
  }
}
