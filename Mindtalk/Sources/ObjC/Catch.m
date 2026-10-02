#import "Catch.h"

BOOL MTCatch(NS_NOESCAPE void (^block)(void), NSError *_Nullable *_Nullable error) {
    @try {
        block();
        return YES;
    } @catch (NSException *exception) {
        if (error) {
            NSMutableDictionary *info = [NSMutableDictionary dictionary];
            info[NSLocalizedFailureReasonErrorKey] = exception.reason ?: exception.name;
            *error = [NSError errorWithDomain:exception.name code:0 userInfo:info];
        }
        return NO;
    }
}
