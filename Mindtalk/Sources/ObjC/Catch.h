#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// Runs `block`, turning an Objective-C exception into an NSError. Swift can't
/// catch these; AVAudioEngine throws them when a microphone changes format
/// under it (AirPods switching to their headset mode), which would otherwise
/// quit the app.
BOOL MTCatch(NS_NOESCAPE void (^block)(void), NSError *_Nullable *_Nullable error);

NS_ASSUME_NONNULL_END
